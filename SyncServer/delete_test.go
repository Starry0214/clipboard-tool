package main

import (
	"encoding/json"
	"testing"
)

func TestDeleteMessagesByHashText(t *testing.T) {
	s := openTestStore(t)
	uid, _ := s.CreateUser("alice", "hash1")
	did, _ := s.CreateDevice(uid, "phone", "th1")
	_, _ = s.InsertMessage(uid, did, "clip_text", []byte(`{"text":"hello 世界"}`))
	_, _ = s.InsertMessage(uid, did, "clip_text", []byte(`{"text":"keep me"}`))

	n, err := s.DeleteMessagesByHash(uid, hashText("hello 世界"))
	if err != nil || n != 1 {
		t.Fatalf("DeleteMessagesByHash = %d, %v; want 1", n, err)
	}
	msgs, _ := s.MessagesSince(uid, 0)
	if len(msgs) != 1 || string(msgs[0].Payload) != `{"text":"keep me"}` {
		t.Fatalf("remaining = %+v", msgs)
	}
}

func TestDeleteMessagesByHashImageAndMedia(t *testing.T) {
	s := openTestStore(t)
	uid, _ := s.CreateUser("alice", "hash1")
	did, _ := s.CreateDevice(uid, "phone", "th1")

	img := []byte("PNGDATA-IMAGE")
	mediaID, _ := s.InsertMedia(uid, img)
	payload, _ := json.Marshal(map[string]any{"mediaId": mediaID, "name": "a.png", "size": len(img)})
	_, _ = s.InsertMessage(uid, did, "clip_image", payload)

	n, err := s.DeleteMessagesByHash(uid, hashBytes(img))
	if err != nil || n != 1 {
		t.Fatalf("DeleteMessagesByHash = %d, %v; want 1", n, err)
	}
	if _, err := s.GetMedia(uid, mediaID); err == nil {
		t.Fatal("media should be removed when no message references it")
	}
	if msgs, _ := s.MessagesSince(uid, 0); len(msgs) != 0 {
		t.Fatalf("messages remain: %+v", msgs)
	}
}

func TestDeleteKeepsReferencedMedia(t *testing.T) {
	s := openTestStore(t)
	uid, _ := s.CreateUser("alice", "hash1")
	did, _ := s.CreateDevice(uid, "phone", "th1")

	img := []byte("PNGDATA-SHARED")
	mediaID, _ := s.InsertMedia(uid, img)
	imgPayload, _ := json.Marshal(map[string]any{"mediaId": mediaID, "name": "a.png", "size": len(img)})
	filePayload, _ := json.Marshal(map[string]any{"mediaId": mediaID, "name": "a.txt", "size": 5})
	_, _ = s.InsertMessage(uid, did, "clip_image", imgPayload)
	_, _ = s.InsertMessage(uid, did, "clip_file", filePayload) // 引用同一媒体，但文件消息不参与哈希匹配

	if _, err := s.DeleteMessagesByHash(uid, hashBytes(img)); err != nil {
		t.Fatalf("delete: %v", err)
	}
	if _, err := s.GetMedia(uid, mediaID); err != nil {
		t.Fatal("media should be kept while another message still references it")
	}
}

func TestDeleteIsolationAcrossUsers(t *testing.T) {
	s := openTestStore(t)
	uidA, _ := s.CreateUser("alice", "hash1")
	didA, _ := s.CreateDevice(uidA, "phone", "th1")
	uidB, _ := s.CreateUser("bob", "hash2")
	didB, _ := s.CreateDevice(uidB, "phone", "th2")

	_, _ = s.InsertMessage(uidA, didA, "clip_text", []byte(`{"text":"secret"}`))
	_, _ = s.InsertMessage(uidB, didB, "clip_text", []byte(`{"text":"secret"}`))

	n, err := s.DeleteMessagesByHash(uidA, hashText("secret"))
	if err != nil || n != 1 {
		t.Fatalf("delete A = %d, %v; want 1", n, err)
	}
	if msgs, _ := s.MessagesSince(uidB, 0); len(msgs) != 1 {
		t.Fatalf("user B messages affected: %+v", msgs)
	}
}
