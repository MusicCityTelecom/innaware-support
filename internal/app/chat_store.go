package app

import (
	"context"
	"database/sql"
	"time"
)

type ChatMessage struct {
	ID         int64     `json:"id"`
	SessionID  string    `json:"session_id"`
	SenderType string    `json:"sender_type"`
	SenderName string    `json:"sender_name"`
	Body       string    `json:"body"`
	CreatedAt  time.Time `json:"created_at"`
}

func (s *Store) migrateChat(ctx context.Context) error {
	_, err := s.db.ExecContext(ctx, `CREATE TABLE IF NOT EXISTS support_chat_messages (
		id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
		session_id VARCHAR(36) NOT NULL,
		sender_type VARCHAR(24) NOT NULL,
		sender_name VARCHAR(120) NOT NULL,
		body TEXT NOT NULL,
		created_at DATETIME(6) NOT NULL,
		INDEX idx_support_chat_session (session_id, created_at, id),
		CONSTRAINT fk_support_chat_session
			FOREIGN KEY (session_id) REFERENCES support_sessions(id)
			ON DELETE CASCADE
	) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci`)
	return err
}

func (s *Store) AddChatMessage(
	ctx context.Context,
	sessionID, senderType, senderName, body string,
) (ChatMessage, error) {
	now := time.Now().UTC()
	result, err := s.db.ExecContext(ctx, `INSERT INTO support_chat_messages
		(session_id, sender_type, sender_name, body, created_at)
		VALUES (?, ?, ?, ?, ?)`,
		sessionID, senderType, senderName, body, now)
	if err != nil {
		return ChatMessage{}, err
	}
	id, err := result.LastInsertId()
	if err != nil {
		return ChatMessage{}, err
	}
	return ChatMessage{
		ID: id,
		SessionID: sessionID,
		SenderType: senderType,
		SenderName: senderName,
		Body: body,
		CreatedAt: now,
	}, nil
}

func (s *Store) ListChatMessages(
	ctx context.Context,
	sessionID string,
	limit int,
) ([]ChatMessage, error) {
	if limit < 1 || limit > 500 {
		limit = 200
	}
	rows, err := s.db.QueryContext(ctx, `SELECT
		id, session_id, sender_type, sender_name, body, created_at
		FROM support_chat_messages
		WHERE session_id=?
		ORDER BY id DESC
		LIMIT ?`, sessionID, limit)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	reversed := make([]ChatMessage, 0, limit)
	for rows.Next() {
		var m ChatMessage
		if err := rows.Scan(
			&m.ID,
			&m.SessionID,
			&m.SenderType,
			&m.SenderName,
			&m.Body,
			&m.CreatedAt,
		); err != nil {
			return nil, err
		}
		reversed = append(reversed, m)
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}

	out := make([]ChatMessage, len(reversed))
	for i := range reversed {
		out[len(reversed)-1-i] = reversed[i]
	}
	return out, nil
}

var _ = sql.ErrNoRows
