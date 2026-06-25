package registry

import (
	"archive/tar"
	"bytes"
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"testing"
)

func quietLogger() *slog.Logger {
	return slog.New(slog.NewTextHandler(io.Discard, nil))
}

// TestExtractFromTarSingleFile: legacy single-file path
func TestExtractFromTarSingleFile(t *testing.T) {
	var buf bytes.Buffer
	tw := tar.NewWriter(&buf)
	body := []byte("#!/bin/sh\necho hi\n")
	_ = tw.WriteHeader(&tar.Header{
		Name: "bin/launcher", Mode: 0755, Size: int64(len(body)), Typeflag: tar.TypeReg,
	})
	_, _ = tw.Write(body)
	_ = tw.Close()

	tmp := t.TempDir()
	dest := filepath.Join(tmp, "launcher")
	if err := os.MkdirAll(filepath.Dir(dest), 0755); err != nil {
		t.Fatalf("MkdirAll: %v", err)
	}

	m := &Manager{logger: quietLogger()}
	layout, err := m.extractFromTar(bytes.NewReader(buf.Bytes()), dest, "launcher")
	if err != nil {
		t.Fatalf("extractFromTar: %v", err)
	}
	if layout != "single-file" {
		t.Errorf("layout = %q, want %q", layout, "single-file")
	}
	got, err := os.ReadFile(dest)
	if err != nil {
		t.Fatalf("ReadFile: %v", err)
	}
	if !bytes.Equal(got, body) {
		t.Errorf("body mismatch: got %q, want %q", got, body)
	}
}

// TestExtractFromTarMultiFile: new multi-file path
func TestExtractFromTarMultiFile(t *testing.T) {
	var buf bytes.Buffer
	tw := tar.NewWriter(&buf)

	launcherBody := []byte("#!/bin/sh\necho launcher\n")
	_ = tw.WriteHeader(&tar.Header{
		Name: "llama/llama-server", Mode: 0755, Size: int64(len(launcherBody)), Typeflag: tar.TypeReg,
	})
	_, _ = tw.Write(launcherBody)

	libBody1 := []byte("libllama-server-impl.so bytes\n")
	_ = tw.WriteHeader(&tar.Header{
		Name: "llama/libllama-server-impl.so", Mode: 0644, Size: int64(len(libBody1)), Typeflag: tar.TypeReg,
	})
	_, _ = tw.Write(libBody1)

	libBody2 := []byte("libggml-cuda.so.0.13.1 bytes\n")
	_ = tw.WriteHeader(&tar.Header{
		Name: "llama/libggml-cuda.so.0.13.1", Mode: 0644, Size: int64(len(libBody2)), Typeflag: tar.TypeReg,
	})
	_, _ = tw.Write(libBody2)

	otherBody := []byte("noise\n")
	_ = tw.WriteHeader(&tar.Header{
		Name: "etc/passwd", Mode: 0644, Size: int64(len(otherBody)), Typeflag: tar.TypeReg,
	})
	_, _ = tw.Write(otherBody)

	_ = tw.Close()

	tmp := t.TempDir()
	dest := filepath.Join(tmp, "bin", "llama-server")
	if err := os.MkdirAll(filepath.Dir(dest), 0755); err != nil {
		t.Fatalf("MkdirAll: %v", err)
	}

	m := &Manager{logger: quietLogger()}
	layout, err := m.extractFromTar(bytes.NewReader(buf.Bytes()), dest, "llama-server")
	if err != nil {
		t.Fatalf("extractFromTar: %v", err)
	}
	if layout != "multi:llama" {
		t.Errorf("layout = %q, want %q", layout, "multi:llama")
	}

	destDir := filepath.Dir(dest)
	want := map[string][]byte{
		"llama-server":            launcherBody,
		"libllama-server-impl.so": libBody1,
		"libggml-cuda.so.0.13.1":   libBody2,
	}
	for name, wantBody := range want {
		p := filepath.Join(destDir, name)
		got, err := os.ReadFile(p)
		if err != nil {
			t.Errorf("ReadFile(%s): %v", p, err)
			continue
		}
		if !bytes.Equal(got, wantBody) {
			t.Errorf("body of %s mismatch: got %q, want %q", name, got, wantBody)
		}
	}

	if _, err := os.Stat(filepath.Join(destDir, "passwd")); !os.IsNotExist(err) {
		t.Errorf("passwd was extracted but should not have been: stat err = %v", err)
	}

	info, err := os.Stat(dest)
	if err != nil {
		t.Fatalf("Stat(%s): %v", dest, err)
	}
	if info.Mode()&0o111 == 0 {
		t.Errorf("launcher not executable: mode = %o", info.Mode())
	}
}

// TestExtractFromTarMissing: no-match path
func TestExtractFromTarMissing(t *testing.T) {
	var buf bytes.Buffer
	tw := tar.NewWriter(&buf)
	_ = tw.WriteHeader(&tar.Header{
		Name: "some/other-file", Mode: 0644, Size: 5, Typeflag: tar.TypeReg,
	})
	_, _ = tw.Write([]byte("hello"))
	_ = tw.Close()

	m := &Manager{logger: quietLogger()}
	layout, err := m.extractFromTar(bytes.NewReader(buf.Bytes()), "/tmp/should/not/exist/launcher", "launcher")
	if err != nil {
		t.Fatalf("extractFromTar: %v", err)
	}
	if layout != "" {
		t.Errorf("layout = %q, want empty", layout)
	}
}
