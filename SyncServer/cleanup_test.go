package main

import (
	"testing"
	"time"
)

func TestStartCleanupRemovesOld(t *testing.T) {
	s := openTestStore(t)
	uid, _ := s.CreateUser("alice", "hash1")
	did, _ := s.CreateDevice(uid, "phone", "th1")
	_, _ = s.InsertMessage(uid, did, "clip_text", []byte(`{"text":"old"}`))

	now := time.Now().UnixMilli()
	if _, err := s.db.Exec(`UPDATE messages SET ts = ? WHERE user_id = ?`, now-retentionMs-1000, uid); err != nil {
		t.Fatal(err)
	}
	stop := make(chan struct{})
	startCleanup(s, stop)
	defer close(stop)

	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if msgs, _ := s.MessagesSince(uid, 0); len(msgs) == 0 {
			return
		}
		time.Sleep(50 * time.Millisecond)
	}
	t.Fatal("old message not cleaned within 2s")
}
