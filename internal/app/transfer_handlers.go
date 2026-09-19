package app

import (
	"database/sql"
	"encoding/json"
	"errors"
	"fmt"
	"mime"
	"net/http"
	"os"
	"strings"
	"time"

	"github.com/gorilla/websocket"
)

func (s *Server) handleTechFileUpload(w http.ResponseWriter, r *http.Request) {
	if !s.checkStateChangingOrigin(r) {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "origin rejected"})
		return
	}

	id := strings.TrimSpace(r.PathValue("id"))
	session, err := s.store.GetSession(r.Context(), id)
	if err != nil {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "session not found"})
		return
	}
	if !session.RequestedFileTransfer {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "file transfer was not approved for this session"})
		return
	}
	if session.Status != "approved" && session.Status != "connected" {
		writeJSON(w, http.StatusConflict, map[string]any{"error": "customer session is not active"})
		return
	}
	if !s.hub.hasAgent(id) {
		writeJSON(w, http.StatusConflict, map[string]any{"error": "customer support app is not connected"})
		return
	}

	file, name, err := readTransferUpload(w, r)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
		return
	}
	defer file.Close()

	item, err := s.transfers.Put(id, "to_agent", name, file)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
		return
	}

	offer, _ := json.Marshal(map[string]any{
		"type":        "file_offer",
		"transfer_id": item.ID,
		"direction":   item.Direction,
		"name":        item.Name,
		"size":        item.Size,
		"expires_at":  item.ExpiresAt,
	})
	if err := s.hub.sendToAgent(id, websocket.TextMessage, offer); err != nil {
		s.transfers.Remove(item.ID)
		writeJSON(w, http.StatusConflict, map[string]any{"error": "customer support app disconnected before transfer offer"})
		return
	}

	admin := currentAdmin(r.Context())
	s.store.AddEvent(r.Context(), id, admin.Username, "file_offered_to_customer",
		fmt.Sprintf("name=%q size=%d", item.Name, item.Size))
	s.store.AddAdminAudit(r.Context(), &admin.ID, admin.Username, "file_offered_to_customer",
		"session", id, auditJSON(map[string]any{"name": item.Name, "size": item.Size}), s.clientIP(r))

	writeJSON(w, http.StatusCreated, map[string]any{"transfer": item})
}

func (s *Server) handleTechFileDownload(w http.ResponseWriter, r *http.Request) {
	id := strings.TrimSpace(r.PathValue("id"))
	transferID := strings.TrimSpace(r.PathValue("transfer"))

	item, ok := s.transfers.Get(transferID)
	if !ok || item.SessionID != id || item.Direction != "to_tech" {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "file transfer not found or expired"})
		return
	}

	session, err := s.store.GetSession(r.Context(), id)
	if err != nil || !session.RequestedFileTransfer {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "file transfer unavailable"})
		return
	}

	if err := serveTransferFile(w, r, item); err != nil {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "file transfer unavailable"})
		return
	}

	admin := currentAdmin(r.Context())
	s.store.AddEvent(r.Context(), id, admin.Username, "file_downloaded_by_technician",
		fmt.Sprintf("name=%q size=%d", item.Name, item.Size))
}

func (s *Server) handleAgentFileUpload(w http.ResponseWriter, r *http.Request) {
	session, err := s.authenticateLiveAgentRequest(r)
	if err != nil {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"error": "invalid session credentials"})
		return
	}
	if !session.RequestedFileTransfer {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "file transfer was not approved for this session"})
		return
	}
	if !s.hub.hasTech(session.ID) {
		writeJSON(w, http.StatusConflict, map[string]any{"error": "technician viewer is not connected"})
		return
	}

	file, name, err := readTransferUpload(w, r)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
		return
	}
	defer file.Close()

	item, err := s.transfers.Put(session.ID, "to_tech", name, file)
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
		return
	}

	offer, _ := json.Marshal(map[string]any{
		"type":        "file_offer",
		"transfer_id": item.ID,
		"direction":   item.Direction,
		"name":        item.Name,
		"size":        item.Size,
		"expires_at":  item.ExpiresAt,
	})
	if err := s.hub.sendToTech(session.ID, websocket.TextMessage, offer); err != nil {
		s.transfers.Remove(item.ID)
		writeJSON(w, http.StatusConflict, map[string]any{"error": "technician viewer disconnected before transfer offer"})
		return
	}

	s.store.AddEvent(r.Context(), session.ID, "customer", "file_offered_to_technician",
		fmt.Sprintf("name=%q size=%d", item.Name, item.Size))
	writeJSON(w, http.StatusCreated, map[string]any{"transfer": item})
}

func (s *Server) handleAgentFileDownload(w http.ResponseWriter, r *http.Request) {
	session, err := s.authenticateLiveAgentRequest(r)
	if err != nil {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"error": "invalid session credentials"})
		return
	}
	if !session.RequestedFileTransfer {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "file transfer was not approved for this session"})
		return
	}

	item, ok := s.transfers.Get(strings.TrimSpace(r.PathValue("transfer")))
	if !ok || item.SessionID != session.ID || item.Direction != "to_agent" {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "file transfer not found or expired"})
		return
	}

	if err := serveTransferFile(w, r, item); err != nil {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "file transfer unavailable"})
		return
	}

	s.store.AddEvent(r.Context(), session.ID, "customer", "file_downloaded_by_customer",
		fmt.Sprintf("name=%q size=%d", item.Name, item.Size))
}

func (s *Server) authenticateLiveAgentRequest(r *http.Request) (Session, error) {
	id := strings.TrimSpace(r.URL.Query().Get("session"))
	token := bearerToken(r)
	if id == "" || token == "" {
		return Session{}, errors.New("missing session credentials")
	}

	session, err := s.store.GetSession(r.Context(), id)
	if err != nil {
		return Session{}, err
	}
	if session.Status == "ended" || session.Status == "expired" ||
		!session.ExpiresAt.After(time.Now().UTC()) ||
		!secureEqual(session.AgentTokenHash, sha256Hex(token)) {
		return Session{}, sql.ErrNoRows
	}
	return session, nil
}

func readTransferUpload(w http.ResponseWriter, r *http.Request) (multipartFile, string, error) {
	r.Body = http.MaxBytesReader(w, r.Body, maxTransferBytes+1024*1024)
	if err := r.ParseMultipartForm(maxTransferBytes + 1024*1024); err != nil {
		return nil, "", errors.New("invalid upload or file exceeds 25 MB limit")
	}

	file, header, err := r.FormFile("file")
	if err != nil {
		return nil, "", errors.New("file is required")
	}
	if header.Size > maxTransferBytes {
		file.Close()
		return nil, "", errors.New("file exceeds 25 MB transfer limit")
	}
	return file, header.Filename, nil
}

type multipartFile interface {
	Read([]byte) (int, error)
	Close() error
}

func serveTransferFile(w http.ResponseWriter, r *http.Request, item FileTransfer) error {
	f, err := os.Open(item.Path)
	if err != nil {
		return err
	}
	defer f.Close()

	st, err := f.Stat()
	if err != nil || !st.Mode().IsRegular() {
		return errors.New("transfer file unavailable")
	}

	disposition := mime.FormatMediaType("attachment", map[string]string{"filename": item.Name})
	w.Header().Set("Content-Type", "application/octet-stream")
	w.Header().Set("Content-Disposition", disposition)
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Content-Length", fmt.Sprintf("%d", item.Size))
	http.ServeContent(w, r, item.Name, item.CreatedAt, f)
	return nil
}
