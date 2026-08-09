package main

import (
	"database/sql"
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
	var out []Device
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

func (s *Store) InsertMessage(userID, originDeviceID int64, msgType string, payload []byte) (int64, error) {
	seq, err := s.nextSeq(userID)
	if err != nil {
		return 0, err
	}
	res, err := s.db.Exec(`INSERT INTO messages(user_id, origin_device_id, type, seq, ts, payload) VALUES(?, ?, ?, ?, ?, ?)`,
		userID, originDeviceID, msgType, seq, time.Now().UnixMilli(), payload)
	if err != nil {
		return 0, err
	}
	return res.LastInsertId()
}

func (s *Store) nextSeq(userID int64) (int64, error) {
	var seq int64
	err := s.db.QueryRow(`SELECT COALESCE(MAX(seq), 0) + 1 FROM messages WHERE user_id = ?`, userID).Scan(&seq)
	return seq, err
}

func (s *Store) MessagesSince(userID int64, since int64) ([]Message, error) {
	rows, err := s.db.Query(`SELECT type, origin_device_id, seq, ts, payload FROM messages WHERE user_id = ? AND ts > ? ORDER BY ts`, userID, since)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	var out []Message
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
