package app

import (
	"errors"
	"io"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

const maxTransferBytes int64 = 25 * 1024 * 1024
const transferTTL = 30 * time.Minute

type FileTransfer struct {
	ID        string    `json:"id"`
	SessionID string    `json:"session_id"`
	Direction string    `json:"direction"`
	Name      string    `json:"name"`
	Size      int64     `json:"size"`
	Path      string    `json:"-"`
	CreatedAt time.Time `json:"created_at"`
	ExpiresAt time.Time `json:"expires_at"`
}

type TransferStore struct {
	mu    sync.Mutex
	dir   string
	items map[string]FileTransfer
}

func NewTransferStore() *TransferStore {
	return &TransferStore{
		dir:   filepath.Join(os.TempDir(), "innaware-support-transfers"),
		items: make(map[string]FileTransfer),
	}
}

func (s *TransferStore) Put(sessionID, direction, name string, src io.Reader) (FileTransfer, error) {
	if direction != "to_agent" && direction != "to_tech" {
		return FileTransfer{}, errors.New("invalid transfer direction")
	}
	if err := os.MkdirAll(s.dir, 0o700); err != nil {
		return FileTransfer{}, err
	}

	id, err := randomToken(18)
	if err != nil {
		return FileTransfer{}, err
	}

	name = filepath.Base(strings.TrimSpace(name))
	if name == "" || name == "." || name == string(filepath.Separator) {
		name = "support-file.bin"
	}
	if len(name) > 180 {
		name = name[:180]
	}

	path := filepath.Join(s.dir, id+".bin")
	f, err := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o600)
	if err != nil {
		return FileTransfer{}, err
	}

	n, copyErr := io.Copy(f, io.LimitReader(src, maxTransferBytes+1))
	closeErr := f.Close()
	if copyErr != nil {
		_ = os.Remove(path)
		return FileTransfer{}, copyErr
	}
	if closeErr != nil {
		_ = os.Remove(path)
		return FileTransfer{}, closeErr
	}
	if n > maxTransferBytes {
		_ = os.Remove(path)
		return FileTransfer{}, errors.New("file exceeds 25 MB transfer limit")
	}

	now := time.Now().UTC()
	item := FileTransfer{
		ID: id, SessionID: sessionID, Direction: direction,
		Name: name, Size: n, Path: path,
		CreatedAt: now, ExpiresAt: now.Add(transferTTL),
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	s.cleanupLocked(now)
	s.items[id] = item
	return item, nil
}

func (s *TransferStore) Get(id string) (FileTransfer, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.cleanupLocked(time.Now().UTC())
	item, ok := s.items[id]
	return item, ok
}

func (s *TransferStore) Remove(id string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if item, ok := s.items[id]; ok {
		_ = os.Remove(item.Path)
		delete(s.items, id)
	}
}

func (s *TransferStore) RemoveSession(sessionID string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	for id, item := range s.items {
		if item.SessionID == sessionID {
			_ = os.Remove(item.Path)
			delete(s.items, id)
		}
	}
}

func (s *TransferStore) cleanupLocked(now time.Time) {
	for id, item := range s.items {
		if !item.ExpiresAt.After(now) {
			_ = os.Remove(item.Path)
			delete(s.items, id)
		}
	}
}
