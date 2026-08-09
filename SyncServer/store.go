package main

import (
	"crypto/sha256"
	"database/sql"
	"encoding/hex"
	"encoding/json"
	"errors"
	"strings"
	"time"

	_ "modernc.org/sqlite"
)

var ErrUsernameTaken = errors.New("username taken")

type Store struct{ db *sql.DB }

type User struct {
	ID           int64
	Username     string
	PasswordHash string
}

type Device struct {
	ID        int64
	UserID    int64
	Name      string
	TokenHash string
	LastSeen  int64
}

type Message struct {
	Type           string          `json:"type"`
	OriginDeviceID int64           `json:"originDeviceId"`
	Seq            int64           `json:"seq"`
	Ts             int64           `json:"ts"`
	Payload        json.RawMessage `json:"payload"`
}

func OpenStore(path string) (*Store, error) {
	db, err := sql.Open("sqlite", path)
	if err != nil {
		return nil, err
	}
	s := &Store{db: db}
	if err := s.migrate(); err != nil {
		db.Close()
		return nil, err
	}
	return s, nil
}

func (s *Store) Close() error { return s.db.Close() }

func (s *Store) migrate() error {
	_, err := s.db.Exec(`
CREATE TABLE IF NOT EXISTS users (
	id INTEGER PRIMARY KEY AUTOINCREMENT,
	username TEXT NOT NULL UNIQUE,
	password_hash TEXT NOT NULL,
	created_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS devices (
	id INTEGER PRIMARY KEY AUTOINCREMENT,
	user_id INTEGER NOT NULL REFERENCES users(id),
	name TEXT NOT NULL,
	token_hash TEXT NOT NULL UNIQUE,
	last_seen INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS messages (
	id INTEGER PRIMARY KEY AUTOINCREMENT,
	user_id INTEGER NOT NULL REFERENCES users(id),
	origin_device_id INTEGER NOT NULL,
	type TEXT NOT NULL,
	seq INTEGER NOT NULL,
	ts INTEGER NOT NULL,
	payload BLOB NOT NULL
);
CREATE TABLE IF NOT EXISTS media (
	id INTEGER PRIMARY KEY AUTOINCREMENT,
	user_id INTEGER NOT NULL REFERENCES users(id),
	data BLOB NOT NULL,
	created_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_messages_user_ts ON messages(user_id, ts);
CREATE INDEX IF NOT EXISTS idx_media_user ON media(user_id, created_at);`)
	return err
}

func (s *Store) CreateUser(username, passwordHash string) (int64, error) {
	res, err := s.db.Exec(`INSERT INTO users(username, password_hash, created_at) VALUES(?, ?, ?)`,
		username, passwordHash, time.Now().UnixMilli())
	if err != nil {
		if strings.Contains(err.Error(), "UNIQUE") {
			return 0, ErrUsernameTaken
		}
		return 0, err
	}
	return res.LastInsertId()
}

func (s *Store) GetUserByUsername(username string) (User, error) {
	var u User
	err := s.db.QueryRow(`SELECT id, username, password_hash FROM users WHERE username = ?`, username).
		Scan(&u.ID, &u.Username, &u.PasswordHash)
	return u, err
}

func (s *Store) CreateDevice(userID int64, name, tokenHash string) (int64, error) {
	res, err := s.db.Exec(`INSERT INTO devices(user_id, name, token_hash, last_seen) VALUES(?, ?, ?, ?)`,
		userID, name, tokenHash, time.Now().UnixMilli())
	if err != nil {
		return 0, err
	}
	return res.LastInsertId()
}

func (s *Store) GetDeviceByToken(tokenHash string) (Device, error) {
	var d Device
	err := s.db.QueryRow(`SELECT id, user_id, name, token_hash, last_seen FROM devices WHERE token_hash = ?`, tokenHash).
		Scan(&d.ID, &d.UserID, &d.Name, &d.TokenHash, &d.LastSeen)
	return d, err
}

func (s *Store) ListDevices(userID int64) ([]Device, error) {
	rows, err := s.db.Query(`SELECT id, user_id, name, token_hash, last_seen FROM devices WHERE user_id = ?`, userID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := make([]Device, 0)
	for rows.Next() {
		var d Device
		if err := rows.Scan(&d.ID, &d.UserID, &d.Name, &d.TokenHash, &d.LastSeen); err != nil {
			return nil, err
		}
		out = append(out, d)
	}
	return out, rows.Err()
}

func (s *Store) DeleteDevice(userID, deviceID int64) error {
	_, err := s.db.Exec(`DELETE FROM devices WHERE id = ? AND user_id = ?`, deviceID, userID)
	return err
}

func (s *Store) TouchDevice(deviceID int64) error {
	_, err := s.db.Exec(`UPDATE devices SET last_seen = ? WHERE id = ?`, time.Now().UnixMilli(), deviceID)
	return err
}

// InsertMessage 插入消息，seq 取行 id（AUTOINCREMENT 永不复用）：
// 删除消息后 MAX(seq)+1 会回落重用，破坏客户端 seq 去重，故不能用。
func (s *Store) InsertMessage(userID, originDeviceID int64, msgType string, payload []byte) (int64, error) {
	res, err := s.db.Exec(`INSERT INTO messages(user_id, origin_device_id, type, seq, ts, payload) VALUES(?, ?, ?, ?, ?, ?)`,
		userID, originDeviceID, msgType, 0, time.Now().UnixMilli(), payload)
	if err != nil {
		return 0, err
	}
	id, err := res.LastInsertId()
	if err != nil {
		return 0, err
	}
	if _, err := s.db.Exec(`UPDATE messages SET seq = ? WHERE id = ?`, id, id); err != nil {
		return 0, err
	}
	return id, nil
}

func (s *Store) MessagesSince(userID int64, since int64) ([]Message, error) {
	rows, err := s.db.Query(`SELECT type, origin_device_id, seq, ts, payload FROM messages WHERE user_id = ? AND ts > ? ORDER BY ts`, userID, since)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := make([]Message, 0)
	for rows.Next() {
		var m Message
		if err := rows.Scan(&m.Type, &m.OriginDeviceID, &m.Seq, &m.Ts, &m.Payload); err != nil {
			return nil, err
		}
		out = append(out, m)
	}
	return out, rows.Err()
}

func (s *Store) InsertMedia(userID int64, data []byte) (int64, error) {
	res, err := s.db.Exec(`INSERT INTO media(user_id, data, created_at) VALUES(?, ?, ?)`,
		userID, data, time.Now().UnixMilli())
	if err != nil {
		return 0, err
	}
	return res.LastInsertId()
}

func (s *Store) GetMedia(userID, mediaID int64) ([]byte, error) {
	var data []byte
	err := s.db.QueryRow(`SELECT data FROM media WHERE id = ? AND user_id = ?`, mediaID, userID).Scan(&data)
	return data, err
}

// DeleteMessagesByHash 删除该账号下内容哈希匹配的消息（文本按 "text\0内容" 哈希、图片按字节哈希），
// 并清理不再被任何消息引用的媒体；返回删除的消息数。
func (s *Store) DeleteMessagesByHash(userID int64, hash string) (int64, error) {
	rows, err := s.db.Query(`SELECT id, type, payload FROM messages WHERE user_id = ?`, userID)
	if err != nil {
		return 0, err
	}
	type match struct {
		id      int64
		mediaID int64
	}
	var matches []match
	for rows.Next() {
		var id int64
		var msgType string
		var payload []byte
		if err := rows.Scan(&id, &msgType, &payload); err != nil {
			rows.Close()
			return 0, err
		}
		var p struct {
			Text    string `json:"text"`
			MediaID int64  `json:"mediaId"`
		}
		json.Unmarshal(payload, &p)
		var h string
		switch msgType {
		case "clip_text":
			if p.Text == "" {
				continue
			}
			h = hashText(p.Text)
		case "clip_image":
			if p.MediaID == 0 {
				continue
			}
			data, err := s.GetMedia(userID, p.MediaID)
			if err != nil {
				continue
			}
			h = hashBytes(data)
		default:
			continue // clip_file 本地哈希为路径，无法跨端匹配，不支持彻底删除
		}
		if h != hash {
			continue
		}
		matches = append(matches, match{id, p.MediaID})
	}
	rows.Close()

	for _, m := range matches {
		if _, err := s.db.Exec(`DELETE FROM messages WHERE id = ? AND user_id = ?`, m.id, userID); err != nil {
			return 0, err
		}
		if m.mediaID != 0 {
			refs, err := s.countMediaRefs(userID, m.mediaID)
			if err == nil && refs == 0 {
				s.db.Exec(`DELETE FROM media WHERE id = ? AND user_id = ?`, m.mediaID, userID)
			}
		}
	}
	return int64(len(matches)), nil
}

// countMediaRefs 统计该账号下仍引用指定媒体的消息数（精确解析 payload）。
func (s *Store) countMediaRefs(userID, mediaID int64) (int64, error) {
	rows, err := s.db.Query(`SELECT payload FROM messages WHERE user_id = ? AND type IN ('clip_image','clip_file')`, userID)
	if err != nil {
		return 0, err
	}
	defer rows.Close()
	var n int64
	for rows.Next() {
		var payload []byte
		if err := rows.Scan(&payload); err != nil {
			return 0, err
		}
		var p struct {
			MediaID int64 `json:"mediaId"`
		}
		if json.Unmarshal(payload, &p) == nil && p.MediaID == mediaID {
			n++
		}
	}
	return n, rows.Err()
}

func hashText(text string) string { return hashBytes([]byte("text\x00" + text)) }

func hashBytes(b []byte) string {
	sum := sha256.Sum256(b)
	return hex.EncodeToString(sum[:])
}

func (s *Store) Cleanup(olderThan int64) (int, error) {
	res, err := s.db.Exec(`DELETE FROM messages WHERE ts < ?`, olderThan)
	if err != nil {
		return 0, err
	}
	n, _ := res.RowsAffected()
	res, err = s.db.Exec(`DELETE FROM media WHERE created_at < ?`, olderThan)
	if err != nil {
		return 0, err
	}
	m, _ := res.RowsAffected()
	return int(n + m), nil
}
