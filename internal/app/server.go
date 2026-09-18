package app

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"log"
	"mime"
	"net"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/MusicCityTelecom/innaware-support/internal/webui"
	"github.com/gorilla/websocket"
)

type Server struct {
	cfg          Config
	store        *Store
	hub          *Hub
	mux          *http.ServeMux
	agentLimiter *limiter
	loginLimiter *limiter
	upgrader     websocket.Upgrader
}

func NewServer(cfg Config, store *Store) *Server {
	s := &Server{
		cfg:          cfg,
		store:        store,
		hub:          NewHub(),
		mux:          http.NewServeMux(),
		agentLimiter: newLimiter(12, 10*time.Minute),
		loginLimiter: newLimiter(10, 15*time.Minute),
	}
	s.upgrader = websocket.Upgrader{
		HandshakeTimeout: 10 * time.Second,
		ReadBufferSize:   64 * 1024,
		WriteBufferSize:  64 * 1024,
		CheckOrigin:      s.checkOrigin,
	}
	s.routes()
	return s
}

func (s *Server) Handler() http.Handler {
	return s.securityHeaders(s.logRequests(s.mux))
}

func (s *Server) routes() {
	s.mux.HandleFunc("GET /api/health", s.handleHealth)
	s.mux.HandleFunc("POST /api/login", s.handleLogin)
	s.mux.HandleFunc("POST /api/logout", s.handleLogout)
	s.mux.HandleFunc("GET /api/me", s.requireTech(s.handleMe))
	s.mux.HandleFunc("GET /api/sessions", s.requireTech(s.handleListSessions))
	s.mux.HandleFunc("POST /api/sessions", s.requireTech(s.handleCreateSession))
	s.mux.HandleFunc("GET /api/sessions/{id}", s.requireTech(s.handleGetSession))
	s.mux.HandleFunc("POST /api/sessions/{id}/end", s.requireTech(s.handleEndSession))
	s.mux.HandleFunc("POST /api/agent/lookup", s.handleAgentLookup)
	s.mux.HandleFunc("POST /api/agent/redeem", s.handleAgentRedeem)
	s.mux.HandleFunc("GET /api/download-status", s.handleDownloadStatus)
	s.mux.HandleFunc("GET /download/windows", s.handleAgentDownload)
	s.mux.HandleFunc("GET /ws/agent", s.handleAgentWS)
	s.mux.HandleFunc("GET /ws/tech", s.requireTech(s.handleTechWS))

	webFS, err := fs.Sub(webui.Files, "web")
	if err != nil {
		panic(err)
	}
	fileServer := http.FileServer(http.FS(webFS))
	s.mux.Handle("GET /", fileServer)
}

func (s *Server) securityHeaders(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("X-Frame-Options", "DENY")
		w.Header().Set("Referrer-Policy", "same-origin")
		w.Header().Set("Permissions-Policy", "camera=(), microphone=(), geolocation=()")
		w.Header().Set("Content-Security-Policy", "default-src 'self'; img-src 'self' data: blob:; style-src 'self'; script-src 'self'; connect-src 'self' wss:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'")
		next.ServeHTTP(w, r)
	})
}

func (s *Server) logRequests(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		start := time.Now()
		next.ServeHTTP(w, r)
		if !strings.HasPrefix(r.URL.Path, "/api/health") {
			log.Printf("%s %s from=%s elapsed=%s", r.Method, r.URL.Path, s.clientIP(r), time.Since(start).Round(time.Millisecond))
		}
	})
}

func (s *Server) clientIP(r *http.Request) string {
	if s.cfg.TrustProxy {
		if x := strings.TrimSpace(strings.Split(r.Header.Get("X-Forwarded-For"), ",")[0]); net.ParseIP(x) != nil {
			return x
		}
	}
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err == nil {
		return host
	}
	return r.RemoteAddr
}

func (s *Server) checkOrigin(r *http.Request) bool {
	origin := r.Header.Get("Origin")
	if origin == "" {
		return true // native customer agent
	}
	u, err := url.Parse(origin)
	if err != nil {
		return false
	}
	return strings.EqualFold(u.Host, r.Host)
}

func (s *Server) checkStateChangingOrigin(r *http.Request) bool {
	origin := r.Header.Get("Origin")
	if origin == "" {
		return true
	}
	u, err := url.Parse(origin)
	return err == nil && strings.EqualFold(u.Host, r.Host)
}

func (s *Server) requireTech(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		username, err := authenticateTech(r, s.cfg)
		if err != nil {
			writeJSON(w, http.StatusUnauthorized, map[string]any{"error": "authentication required"})
			return
		}
		ctx := context.WithValue(r.Context(), techContextKey{}, username)
		next(w, r.WithContext(ctx))
	}
}

type techContextKey struct{}

func techName(ctx context.Context) string {
	v, _ := ctx.Value(techContextKey{}).(string)
	return v
}

func decodeJSON(w http.ResponseWriter, r *http.Request, dst any) bool {
	defer r.Body.Close()
	dec := json.NewDecoder(io.LimitReader(r.Body, 64*1024))
	dec.DisallowUnknownFields()
	if err := dec.Decode(dst); err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "invalid JSON request"})
		return false
	}
	return true
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func (s *Server) handleHealth(w http.ResponseWriter, r *http.Request) {
	if err := s.store.Health(r.Context()); err != nil {
		writeJSON(w, http.StatusServiceUnavailable, map[string]any{"status": "degraded", "database": "down"})
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"status": "ok", "database": "ok", "time": time.Now().UTC()})
}

func (s *Server) handleLogin(w http.ResponseWriter, r *http.Request) {
	if !s.checkStateChangingOrigin(r) {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "origin rejected"})
		return
	}
	if !s.loginLimiter.Allow(s.clientIP(r)) {
		writeJSON(w, http.StatusTooManyRequests, map[string]any{"error": "too many login attempts"})
		return
	}
	var req struct {
		Username string `json:"username"`
		Password string `json:"password"`
	}
	if !decodeJSON(w, r, &req) {
		return
	}
	if !secureEqual(req.Username, s.cfg.TechUsername) || !secureEqual(req.Password, s.cfg.TechPassword) {
		time.Sleep(250 * time.Millisecond)
		writeJSON(w, http.StatusUnauthorized, map[string]any{"error": "invalid username or password"})
		return
	}
	if err := issueTechCookie(w, s.cfg); err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not create login session"})
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"ok": true, "username": s.cfg.TechUsername})
}

func (s *Server) handleLogout(w http.ResponseWriter, r *http.Request) {
	clearTechCookie(w)
	writeJSON(w, http.StatusOK, map[string]any{"ok": true})
}

func (s *Server) handleMe(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, map[string]any{"username": techName(r.Context())})
}

func newUUID() (string, error) {
	raw, err := randomToken(16)
	if err != nil {
		return "", err
	}
	b, err := base64URLDecode(raw)
	if err != nil || len(b) != 16 {
		return "", fmt.Errorf("uuid entropy failure")
	}
	b[6] = (b[6] & 0x0f) | 0x40
	b[8] = (b[8] & 0x3f) | 0x80
	return fmt.Sprintf("%08x-%04x-%04x-%04x-%012x",
		b[0:4], b[4:6], b[6:8], b[8:10], b[10:16]), nil
}

func base64URLDecode(v string) ([]byte, error) {
	return base64RawURLDecode(v)
}

func (s *Server) handleCreateSession(w http.ResponseWriter, r *http.Request) {
	if !s.checkStateChangingOrigin(r) {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "origin rejected"})
		return
	}
	var req struct {
		CustomerLabel      string `json:"customer_label"`
		RequestedControl   bool   `json:"requested_control"`
		RequestedElevation bool   `json:"requested_elevation"`
	}
	if !decodeJSON(w, r, &req) {
		return
	}
	if len(req.CustomerLabel) > 160 {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "customer label is too long"})
		return
	}
	id, err := newUUID()
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not create session"})
		return
	}

	var code string
	for i := 0; i < 10; i++ {
		code, err = randomDigits(8)
		if err != nil {
			break
		}
		now := time.Now().UTC()
		session := Session{
			ID: id, CodeHint: code[len(code)-4:], CustomerLabel: strings.TrimSpace(req.CustomerLabel),
			TechnicianName: techName(r.Context()), RequestedControl: req.RequestedControl,
			RequestedElevation: req.RequestedElevation, CreatedAt: now, ExpiresAt: now.Add(s.cfg.SessionTTL),
		}
		err = s.store.CreateSession(r.Context(), session, hmacHex(s.cfg.CodeSecret, code))
		if err == nil {
			s.store.AddEvent(r.Context(), id, "technician", "session_created", "")
			writeJSON(w, http.StatusCreated, map[string]any{"session": session, "code": code})
			return
		}
		if !strings.Contains(strings.ToLower(err.Error()), "duplicate") {
			break
		}
	}
	log.Printf("create session failed: %v", err)
	writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not create session"})
}

func (s *Server) handleListSessions(w http.ResponseWriter, r *http.Request) {
	sessions, err := s.store.ListSessions(r.Context(), 60)
	if err != nil {
		log.Printf("list sessions: %v", err)
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not load sessions"})
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"sessions": sessions})
}

func (s *Server) handleGetSession(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	session, err := s.store.GetSession(r.Context(), id)
	if err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			writeJSON(w, http.StatusNotFound, map[string]any{"error": "session not found"})
			return
		}
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not load session"})
		return
	}
	events, _ := s.store.ListEvents(r.Context(), id)
	writeJSON(w, http.StatusOK, map[string]any{"session": session, "events": events})
}

func (s *Server) handleEndSession(w http.ResponseWriter, r *http.Request) {
	if !s.checkStateChangingOrigin(r) {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "origin rejected"})
		return
	}
	id := r.PathValue("id")
	if err := s.store.EndSession(r.Context(), id); err != nil {
		if errors.Is(err, sql.ErrNoRows) {
			writeJSON(w, http.StatusNotFound, map[string]any{"error": "session not found"})
			return
		}
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not end session"})
		return
	}
	s.store.AddEvent(r.Context(), id, "technician", "session_ended", "")
	s.hub.End(id)
	writeJSON(w, http.StatusOK, map[string]any{"ok": true})
}

func normalizeCode(code string) string {
	var b strings.Builder
	for _, r := range code {
		if r >= '0' && r <= '9' {
			b.WriteRune(r)
		}
	}
	return b.String()
}

func (s *Server) handleAgentLookup(w http.ResponseWriter, r *http.Request) {
	ip := s.clientIP(r)
	if !s.agentLimiter.Allow(ip) {
		writeJSON(w, http.StatusTooManyRequests, map[string]any{"error": "too many attempts; wait and try again"})
		return
	}
	var req struct {
		Code string `json:"code"`
	}
	if !decodeJSON(w, r, &req) {
		return
	}
	code := normalizeCode(req.Code)
	if len(code) != 8 {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "support session not found"})
		return
	}
	session, err := s.store.LookupByCodeHash(r.Context(), hmacHex(s.cfg.CodeSecret, code))
	if err != nil {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "support session not found or expired"})
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{
		"session_id": session.ID, "technician_name": session.TechnicianName,
		"customer_label": session.CustomerLabel, "requested_control": session.RequestedControl,
		"requested_elevation": session.RequestedElevation, "expires_at": session.ExpiresAt,
	})
}

func (s *Server) handleAgentRedeem(w http.ResponseWriter, r *http.Request) {
	ip := s.clientIP(r)
	if !s.agentLimiter.Allow(ip) {
		writeJSON(w, http.StatusTooManyRequests, map[string]any{"error": "too many attempts; wait and try again"})
		return
	}
	var req struct {
		Code          string `json:"code"`
		MachineName   string `json:"machine_name"`
		TermsAccepted bool   `json:"terms_accepted"`
	}
	if !decodeJSON(w, r, &req) {
		return
	}
	if !req.TermsAccepted {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": "terms and remote-support consent must be accepted"})
		return
	}
	code := normalizeCode(req.Code)
	if len(code) != 8 || len(req.MachineName) > 255 {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "support session not found"})
		return
	}
	token, err := randomToken(32)
	if err != nil {
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not authorize support"})
		return
	}
	session, err := s.store.RedeemSession(r.Context(), hmacHex(s.cfg.CodeSecret, code), sha256Hex(token), strings.TrimSpace(req.MachineName), true)
	if err != nil {
		writeJSON(w, http.StatusConflict, map[string]any{"error": "support session was already used, expired, or ended"})
		return
	}
	s.store.AddEvent(r.Context(), session.ID, "customer", "consent_granted", fmt.Sprintf("machine=%q", session.MachineName))
	base, _ := url.Parse(s.cfg.PublicBaseURL)
	if base.Scheme == "https" {
		base.Scheme = "wss"
	} else {
		base.Scheme = "ws"
	}
	base.Path = "/ws/agent"
	q := base.Query()
	q.Set("session", session.ID)
	base.RawQuery = q.Encode()
	writeJSON(w, http.StatusOK, map[string]any{"session_id": session.ID, "agent_token": token, "websocket_url": base.String()})
}

func (s *Server) handleDownloadStatus(w http.ResponseWriter, r *http.Request) {
	st, err := os.Stat(s.cfg.AgentDownloadPath)
	available := err == nil && st.Mode().IsRegular() && st.Size() > 0
	writeJSON(w, http.StatusOK, map[string]any{"available": available})
}

func (s *Server) handleAgentDownload(w http.ResponseWriter, r *http.Request) {
	st, err := os.Stat(s.cfg.AgentDownloadPath)
	if err != nil || !st.Mode().IsRegular() {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "Windows support agent build is not published on this server yet"})
		return
	}
	w.Header().Set("Content-Type", "application/vnd.microsoft.portable-executable")
	w.Header().Set("Content-Disposition", `attachment; filename="InnAware-Remote-Support.exe"`)
	w.Header().Set("Cache-Control", "no-store")
	http.ServeFile(w, r, s.cfg.AgentDownloadPath)
}

func bearerToken(r *http.Request) string {
	header := strings.TrimSpace(r.Header.Get("Authorization"))
	if len(header) < 8 || !strings.EqualFold(header[:7], "Bearer ") {
		return ""
	}
	return strings.TrimSpace(header[7:])
}

func (s *Server) handleAgentWS(w http.ResponseWriter, r *http.Request) {
	id := r.URL.Query().Get("session")
	token := bearerToken(r)
	if id == "" || token == "" {
		http.Error(w, "missing session credentials", http.StatusUnauthorized)
		return
	}
	session, err := s.store.GetSession(r.Context(), id)
	if err != nil || session.Status == "ended" || session.Status == "expired" || !secureEqual(session.AgentTokenHash, sha256Hex(token)) {
		http.Error(w, "invalid session credentials", http.StatusUnauthorized)
		return
	}
	conn, err := s.upgrader.Upgrade(w, r, nil)
	if err != nil {
		return
	}
	peer := &wsPeer{conn: conn}
	if old := s.hub.setAgent(id, peer); old != nil {
		old.close(websocket.ClosePolicyViolation, "replaced by new customer connection")
	}
	defer func() {
		s.hub.unsetAgent(id, peer)
		_ = s.store.MarkAgentDisconnected(context.Background(), id)
		s.store.AddEvent(context.Background(), id, "system", "agent_disconnected", "")
		_ = s.hub.sendToTech(id, websocket.TextMessage, []byte(`{"type":"agent_status","status":"disconnected"}`))
		_ = conn.Close()
	}()
	_ = s.store.MarkAgentConnected(r.Context(), id)
	s.store.AddEvent(r.Context(), id, "system", "agent_connected", "")
	_ = s.hub.sendToTech(id, websocket.TextMessage, []byte(`{"type":"agent_status","status":"connected"}`))
	conn.SetReadLimit(8 * 1024 * 1024)
	for {
		mt, data, err := conn.ReadMessage()
		if err != nil {
			return
		}
		if mt == websocket.BinaryMessage || mt == websocket.TextMessage {
			if err := s.hub.sendToTech(id, mt, data); err != nil {
				log.Printf("forward agent->tech session=%s: %v", id, err)
			}
		}
	}
}

func (s *Server) handleTechWS(w http.ResponseWriter, r *http.Request) {
	id := r.URL.Query().Get("session")
	session, err := s.store.GetSession(r.Context(), id)
	if err != nil || session.Status == "ended" || session.Status == "expired" {
		http.Error(w, "session unavailable", http.StatusNotFound)
		return
	}
	conn, err := s.upgrader.Upgrade(w, r, nil)
	if err != nil {
		return
	}
	peer := &wsPeer{conn: conn}
	if old := s.hub.setTech(id, peer); old != nil {
		old.close(websocket.ClosePolicyViolation, "replaced by new technician connection")
	}
	defer func() {
		s.hub.unsetTech(id, peer)
		_ = conn.Close()
	}()
	_ = peer.write(websocket.TextMessage, []byte(`{"type":"tech_status","status":"connected"}`))
	conn.SetReadLimit(64 * 1024)
	for {
		mt, data, err := conn.ReadMessage()
		if err != nil {
			return
		}
		if mt != websocket.TextMessage {
			continue
		}
		var envelope struct {
			Type string `json:"type"`
		}
		if json.Unmarshal(data, &envelope) != nil {
			continue
		}
		if envelope.Type == "input" && session.RequestedControl {
			if err := s.hub.sendToAgent(id, websocket.TextMessage, data); err != nil {
				log.Printf("forward tech->agent session=%s: %v", id, err)
			}
		}
	}
}

func mimeInit() {
	_ = mime.AddExtensionType(".js", "text/javascript; charset=utf-8")
	_ = mime.AddExtensionType(".css", "text/css; charset=utf-8")
	_ = mime.AddExtensionType(".svg", "image/svg+xml")
}

func init() { mimeInit() }

// filepath.Clean reference retained here to make it explicit that download paths are local configuration,
// not request-derived paths. This prevents future refactors from accidentally treating the URL as a filesystem path.
var _ = filepath.Clean
