package app

import (
	"bytes"
	"errors"
	"io"
	"mime"
	"net/http"
	"os"
	"path/filepath"
	"strings"

	"github.com/gorilla/websocket"
)

const maxChatImageBytes int64 = 5 * 1024 * 1024

var allowedChatImageMIMEs = map[string]string{
	"image/jpeg": ".jpg",
	"image/png":  ".png",
	"image/gif":  ".gif",
	"image/webp": ".webp",
}

func readChatImageUpload(w http.ResponseWriter, r *http.Request) ([]byte, string, string, error) {
	r.Body = http.MaxBytesReader(w, r.Body, maxChatImageBytes+1024*1024)
	if err := r.ParseMultipartForm(1 * 1024 * 1024); err != nil {
		return nil, "", "", errors.New("invalid image upload or image exceeds 5 MB")
	}
	file, header, err := r.FormFile("image")
	if err != nil {
		return nil, "", "", errors.New("image is required")
	}
	defer file.Close()

	data, err := io.ReadAll(io.LimitReader(file, maxChatImageBytes+1))
	if err != nil {
		return nil, "", "", errors.New("could not read image")
	}
	if int64(len(data)) > maxChatImageBytes {
		return nil, "", "", errors.New("image exceeds 5 MB limit")
	}
	if len(data) == 0 {
		return nil, "", "", errors.New("image is empty")
	}

	detected := http.DetectContentType(data)
	if _, ok := allowedChatImageMIMEs[detected]; !ok {
		return nil, "", "", errors.New("only JPEG, PNG, GIF, and WebP images are allowed")
	}

	name := filepath.Base(strings.TrimSpace(header.Filename))
	if name == "" || name == "." {
		name = "support-image" + allowedChatImageMIMEs[detected]
	}
	if len(name) > 180 {
		ext := filepath.Ext(name)
		base := strings.TrimSuffix(name, ext)
		maxBase := 180 - len(ext)
		if maxBase < 1 {
			maxBase = 1
		}
		if len(base) > maxBase {
			base = base[:maxBase]
		}
		name = base + ext
	}

	return data, name, detected, nil
}

func (s *Server) handleTechChatImageUpload(w http.ResponseWriter, r *http.Request) {
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
		writeJSON(w, http.StatusConflict, map[string]any{"error": "support session is not active"})
		return
	}

	data, name, mimeType, err := readChatImageUpload(w, r)
	if r.MultipartForm != nil {
		defer r.MultipartForm.RemoveAll()
	}
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
		return
	}

	item, err := s.transfers.Put(id, "to_agent", name, bytes.NewReader(data))
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
		return
	}

	message, err := s.store.AddChatMessageWithAttachment(
		r.Context(), id, "technician", techName(r.Context()), "",
		item.ID, item.Name, mimeType, item.Size,
	)
	if err != nil {
		s.transfers.Remove(item.ID)
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not persist image message"})
		return
	}

	wire := chatMessageEnvelope(message)
	_ = s.hub.sendToAgent(id, websocket.TextMessage, wire)
	_ = s.hub.sendToTech(id, websocket.TextMessage, wire)

	admin := currentAdmin(r.Context())
	s.store.AddEvent(r.Context(), id, admin.Username, "chat_image_sent", "technician sent image")
	writeJSON(w, http.StatusCreated, map[string]any{"message": message})
}

func (s *Server) handleAgentChatImageUpload(w http.ResponseWriter, r *http.Request) {
	session, err := s.authenticateLiveAgentRequest(r)
	if err != nil {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"error": "invalid session credentials"})
		return
	}
	if !session.RequestedFileTransfer {
		writeJSON(w, http.StatusForbidden, map[string]any{"error": "file transfer was not approved for this session"})
		return
	}

	data, name, mimeType, err := readChatImageUpload(w, r)
	if r.MultipartForm != nil {
		defer r.MultipartForm.RemoveAll()
	}
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
		return
	}

	item, err := s.transfers.Put(session.ID, "to_tech", name, bytes.NewReader(data))
	if err != nil {
		writeJSON(w, http.StatusBadRequest, map[string]any{"error": err.Error()})
		return
	}

	message, err := s.store.AddChatMessageWithAttachment(
		r.Context(), session.ID, "customer", session.MachineName, "",
		item.ID, item.Name, mimeType, item.Size,
	)
	if err != nil {
		s.transfers.Remove(item.ID)
		writeJSON(w, http.StatusInternalServerError, map[string]any{"error": "could not persist image message"})
		return
	}

	wire := chatMessageEnvelope(message)
	_ = s.hub.sendToTech(session.ID, websocket.TextMessage, wire)
	_ = s.hub.sendToAgent(session.ID, websocket.TextMessage, wire)

	s.store.AddEvent(r.Context(), session.ID, "customer", "chat_image_sent", "customer sent image")
	writeJSON(w, http.StatusCreated, map[string]any{"message": message})
}

func (s *Server) handleTechChatImageDownload(w http.ResponseWriter, r *http.Request) {
	id := strings.TrimSpace(r.PathValue("id"))
	transferID := strings.TrimSpace(r.PathValue("transfer"))
	item, ok := s.transfers.Get(transferID)
	if !ok || item.SessionID != id {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "chat image expired or unavailable"})
		return
	}
	serveChatImage(w, r, item)
}

func (s *Server) handleAgentChatImageDownload(w http.ResponseWriter, r *http.Request) {
	session, err := s.authenticateLiveAgentRequest(r)
	if err != nil {
		writeJSON(w, http.StatusUnauthorized, map[string]any{"error": "invalid session credentials"})
		return
	}
	transferID := strings.TrimSpace(r.PathValue("transfer"))
	item, ok := s.transfers.Get(transferID)
	if !ok || item.SessionID != session.ID {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "chat image expired or unavailable"})
		return
	}
	serveChatImage(w, r, item)
}

func serveChatImage(w http.ResponseWriter, r *http.Request, item FileTransfer) {
	data, err := os.ReadFile(item.Path)
	if err != nil {
		writeJSON(w, http.StatusNotFound, map[string]any{"error": "chat image unavailable"})
		return
	}
	contentType := http.DetectContentType(data)
	if _, ok := allowedChatImageMIMEs[contentType]; !ok {
		writeJSON(w, http.StatusUnsupportedMediaType, map[string]any{"error": "attachment is not a supported image"})
		return
	}

	w.Header().Set("Content-Type", contentType)
	w.Header().Set("Content-Disposition", mime.FormatMediaType("inline", map[string]string{"filename": item.Name}))
	w.Header().Set("Cache-Control", "private, no-store")
	http.ServeContent(w, r, item.Name, item.CreatedAt, bytes.NewReader(data))
}
