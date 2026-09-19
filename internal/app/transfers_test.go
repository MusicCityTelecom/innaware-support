package app

import (
	"bytes"
	"io"
	"os"
	"path/filepath"
	"testing"
	"time"
)

type zeroReader struct{}

func (zeroReader) Read(p []byte) (int, error) {
	for i := range p {
		p[i] = 0
	}
	return len(p), nil
}

func TestTransferStorePutGetAndRemoveSession(t *testing.T) {
	store := NewTransferStore()
	store.dir = t.TempDir()

	item, err := store.Put("session-a", "to_agent", "../example.txt", bytes.NewBufferString("hello"))
	if err != nil {
		t.Fatal(err)
	}
	if item.Name != "example.txt" {
		t.Fatalf("filename was not sanitized: %q", item.Name)
	}
	if item.Size != 5 {
		t.Fatalf("unexpected size: %d", item.Size)
	}
	if filepath.Dir(item.Path) != store.dir {
		t.Fatalf("transfer escaped temp directory: %s", item.Path)
	}

	got, ok := store.Get(item.ID)
	if !ok || got.ID != item.ID {
		t.Fatal("stored transfer was not retrievable")
	}
	data, err := os.ReadFile(item.Path)
	if err != nil {
		t.Fatal(err)
	}
	if string(data) != "hello" {
		t.Fatalf("unexpected transfer content: %q", data)
	}

	store.RemoveSession("session-a")
	if _, ok := store.Get(item.ID); ok {
		t.Fatal("session transfer remained after RemoveSession")
	}
	if _, err := os.Stat(item.Path); !os.IsNotExist(err) {
		t.Fatalf("transfer file still exists after RemoveSession: %v", err)
	}
}

func TestTransferStoreRejectsDirectionAndOversize(t *testing.T) {
	store := NewTransferStore()
	store.dir = t.TempDir()

	if _, err := store.Put("session-a", "sideways", "x.txt", bytes.NewBufferString("x")); err == nil {
		t.Fatal("invalid direction was accepted")
	}

	reader := io.LimitReader(zeroReader{}, maxTransferBytes+1)
	if _, err := store.Put("session-a", "to_tech", "too-large.bin", reader); err == nil {
		t.Fatal("oversize transfer was accepted")
	}

	entries, err := os.ReadDir(store.dir)
	if err != nil {
		t.Fatal(err)
	}
	if len(entries) != 0 {
		t.Fatalf("oversize transfer left temporary files behind: %d", len(entries))
	}
}

func TestTransferStoreListSessionFiltersAndOrders(t *testing.T) {
	store := NewTransferStore()
	store.dir = t.TempDir()

	first, err := store.Put("session-a", "to_tech", "first.txt", bytes.NewBufferString("one"))
	if err != nil {
		t.Fatal(err)
	}
	time.Sleep(time.Millisecond)
	second, err := store.Put("session-a", "to_tech", "second.txt", bytes.NewBufferString("two"))
	if err != nil {
		t.Fatal(err)
	}
	if _, err := store.Put("session-a", "to_agent", "customer.txt", bytes.NewBufferString("x")); err != nil {
		t.Fatal(err)
	}
	if _, err := store.Put("session-b", "to_tech", "other.txt", bytes.NewBufferString("y")); err != nil {
		t.Fatal(err)
	}

	items := store.ListSession("session-a", "to_tech")
	if len(items) != 2 {
		t.Fatalf("expected 2 pending technician files, got %d", len(items))
	}
	if items[0].ID != first.ID || items[1].ID != second.ID {
		t.Fatalf("pending transfer order changed: %+v", items)
	}

	all := store.ListSession("session-a", "")
	if len(all) != 3 {
		t.Fatalf("expected all 3 session-a transfers, got %d", len(all))
	}
}
