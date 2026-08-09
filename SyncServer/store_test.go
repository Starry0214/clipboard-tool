package main

import (
	"errors"
	"testing"
	"time"
)

func openTestStore(t *testing.T) *Store {
	t.Helper()
	s, err := OpenStore("file::memory:?cache=shared")
	if err != nil {
		t.Fatalf("OpenStore: %v", err)
	}
	t.Cleanup(func() { s.Close() })
	return s
}

func TestUserCreateAndGet(t *testing.T) {
	s := openTestStore(t)
	id, err := s.CreateUser("alice", "hash1")
	if err != nil {
		t.Fatalf("CreateUser: %v", err)
	}
	u, err := s.GetUserByUsername("alice")
	if err != nil || u.ID != id || u.PasswordHash != "hash1" {
		t.Fatalf("GetUserByUsername = %+v, %v", u, err)
	}
	if _, err := s.CreateUser("alice", "hash2"); !errors.Is(err, ErrUsernameTaken) {
		t.Fatalf("duplicate username err = %v, want ErrUsernameTaken", err)
	}
	if _, err := s.GetUserByUsername("bob"); err == nil {
		t.Fatal("GetUserByUsername(bob) should fail")
	}
}

func TestDeviceCRUD(t *testing.T) {
	s := openTestStore(t)
	uid, _ := s.CreateUser("alice", "hash1")
	did, err := s.CreateDevice(uid, "小米14 Pro", "tokhash1")
	if err != nil {
		t.Fatalf("CreateDevice: %v", err)
	}
	d, err := s.GetDeviceByToken("tokhash1")
	if err != nil || d.ID != did || d.UserID != uid || d.Name != "小米14 Pro" {
		t.Fatalf("GetDeviceByToken = %+v, %v", d, err)
	}
	if _, err := s.GetDeviceByToken("nope"); err == nil {
		t.Fatal("GetDeviceByToken(nope) should fail")
	}
	if err := s.TouchDevice(did); err != nil {
		t.Fatalf("TouchDevice: %v", err)
	}
	if err := s.DeleteDevice(uid, did); err != nil {
		t.Fatalf("DeleteDevice: %v", err)
	}
	if _, err := s.GetDeviceByToken("tokhash1"); err == nil {
		t.Fatal("device should be gone after delete")
	}
	if err := s.DeleteDevice(uid, 99999); err != nil {
		t.Fatalf("DeleteDevice(missing) = %v, want nil", err)
	}
}

func TestMessagesAndMedia(t *testing.T) {
	s := openTestStore(t)
	uid, _ := s.CreateUser("alice", "hash1")
	did, _ := s.CreateDevice(uid, "phone", "th1")

	payload := []byte(`{"text":"hello"}`)
	mid, err := s.InsertMessage(uid, did, "clip_text", payload)
	if err != nil || mid == 0 {
		t.Fatalf("InsertMessage = %d, %v", mid, err)
	}
	msgs, err := s.MessagesSince(uid, 0)
	if err != nil || len(msgs) != 1 || msgs[0].Type != "clip_text" || string(msgs[0].Payload) != string(payload) || msgs[0].Seq != 1 {
		t.Fatalf("MessagesSince = %+v, %v", msgs, err)
	}
	if got, err := s.MessagesSince(uid, msgs[0].Ts); err != nil || len(got) != 0 {
		t.Fatalf("MessagesSince(since=ts) = %+v, %v", got, err)
	}

	mediaID, err := s.InsertMedia(uid, []byte("PNGDATA"))
	if err != nil || mediaID == 0 {
		t.Fatalf("InsertMedia = %d, %v", mediaID, err)
	}
	data, err := s.GetMedia(uid, mediaID)
	if err != nil || string(data) != "PNGDATA" {
		t.Fatalf("GetMedia = %q, %v", data, err)
	}
	if _, err := s.GetMedia(uid, 99999); err == nil {
		t.Fatal("GetMedia(missing) should fail")
	}
	if _, err := s.GetMedia(uid+1, mediaID); err == nil {
		t.Fatal("GetMedia(other user) should fail")
	}
}

func TestCleanup(t *testing.T) {
	s := openTestStore(t)
	uid, _ := s.CreateUser("alice", "hash1")
	did, _ := s.CreateDevice(uid, "phone", "th1")
	_, _ = s.InsertMessage(uid, did, "clip_text", []byte(`{"text":"old"}`))
	_, _ = s.InsertMedia(uid, []byte("OLD"))

	now := time.Now().UnixMilli()
	if _, err := s.db.Exec(`UPDATE messages SET ts = ? WHERE user_id = ?`, now-8*24*3600*1000, uid); err != nil {
		t.Fatal(err)
	}
	if _, err := s.db.Exec(`UPDATE media SET created_at = ? WHERE user_id = ?`, now-8*24*3600*1000, uid); err != nil {
		t.Fatal(err)
	}
	n, err := s.Cleanup(now - 7*24*3600*1000)
	if err != nil || n != 2 {
		t.Fatalf("Cleanup = %d, %v; want 2", n, err)
	}
	if msgs, _ := s.MessagesSince(uid, 0); len(msgs) != 0 {
		t.Fatalf("messages remain: %+v", msgs)
	}
}
