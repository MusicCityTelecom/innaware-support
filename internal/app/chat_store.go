package app

import (
	"context"
	"time"
)

type ChatMessage struct {
	ID         int64     `json:"id"`
	SessionID  string    `json:"session_id"`
	SenderType string    `json:"sender_type"`
	SenderName string    `json:"sender_name"`
	Body                 string    `json:"body"`
	AttachmentTransferID string    `json:"attachment_transfer_id,omitempty"`
	AttachmentName       string    `json:"attachment_name,omitempty"`
	AttachmentMIME       string    `json:"attachment_mime,omitempty"`
	AttachmentSize       int64     `json:"attachment_size,omitempty"`
	CreatedAt            time.Time `json:"created_at"`
}

func (s *Store) migrateChat(ctx context.Context) error {
	_, err := s.db.ExecContext(ctx, `CREATE TABLE IF NOT EXISTS support_chat_messages (
		id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
		session_id VARCHAR(36) NOT NULL,
		sender_type VARCHAR(24) NOT NULL,
		sender_name VARCHAR(120) NOT NULL,
		body TEXT NOT NULL,
		attachment_transfer_id VARCHAR(64) NOT NULL DEFAULT '',
		attachment_name VARCHAR(180) NOT NULL DEFAULT '',
		attachment_mime VARCHAR(80) NOT NULL DEFAULT '',
		attachment_size BIGINT UNSIGNED NOT NULL DEFAULT 0,
		created_at DATETIME(6) NOT NULL,
		INDEX idx_support_chat_session (session_id, created_at, id),
		CONSTRAINT fk_support_chat_session
			FOREIGN KEY (session_id) REFERENCES support_sessions(id)
			ON DELETE CASCADE
	) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci`)
	if err != nil {
		return err
	}
	for _, column := range []struct {
		name string
		ddl  string
	}{
		{"attachment_transfer_id", "VARCHAR(64) NOT NULL DEFAULT '' AFTER body"},
		{"attachment_name", "VARCHAR(180) NOT NULL DEFAULT '' AFTER attachment_transfer_id"},
		{"attachment_mime", "VARCHAR(80) NOT NULL DEFAULT '' AFTER attachment_name"},
		{"attachment_size", "BIGINT UNSIGNED NOT NULL DEFAULT 0 AFTER attachment_mime"},
	} {
		if err := s.ensureColumn(ctx, "support_chat_messages", column.name, column.ddl); err != nil {
			return err
		}
	}
	return nil
}

func (s *Store) AddChatMessage(
	ctx context.Context,
	sessionID, senderType, senderName, body string,
) (ChatMessage, error) {
	return s.AddChatMessageWithAttachment(
		ctx,
		sessionID,
		senderType,
		senderName,
		body,
		"",
		"",
		"",
		0,
	)
}

func (s *Store) AddChatMessageWithAttachment(
	ctx context.Context,
	sessionID, senderType, senderName, body string,
	attachmentTransferID, attachmentName, attachmentMIME string,
	attachmentSize int64,
) (ChatMessage, error) {
	now := time.Now().UTC()
	result, err := s.db.ExecContext(ctx, `INSERT INTO support_chat_messages
		(session_id, sender_type, sender_name, body,
		 attachment_transfer_id, attachment_name, attachment_mime, attachment_size,
		 created_at)
		VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`,
		sessionID, senderType, senderName, body,
		attachmentTransferID, attachmentName, attachmentMIME, attachmentSize,
		now)
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
		AttachmentTransferID: attachmentTransferID,
		AttachmentName: attachmentName,
		AttachmentMIME: attachmentMIME,
		AttachmentSize: attachmentSize,
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
		id, session_id, sender_type, sender_name, body,
		attachment_transfer_id, attachment_name, attachment_mime, attachment_size,
		created_at
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
			&m.AttachmentTransferID,
			&m.AttachmentName,
			&m.AttachmentMIME,
			&m.AttachmentSize,
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
