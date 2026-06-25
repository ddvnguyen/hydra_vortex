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

func buildTar(t *testing.T, entries []struct {
	name string
	mode int64
	body string
}) []byte {
	t.Helper()
	var buf bytes.Buffer
	tw := tar.NewWriter(&buf)
	for _, e := range entries {
		body := []byte(e.body)
		if err := tw.WriteHeader(&tar.Header{
			Name:     e.name,
			Mode:     e.mode,
			Size:     int64(len(body)),
			Typeflag: tar.TypeReg,
		}); err != nil {
			t.Fatalf("WriteHeader(%s): %v", e.name, err)
		}
		if _, err := tw.Write(body); err != nil {
			t.Fatalf("Write(%s): %v", e.name, err)
		}
	}
	if err := tw.Close(); err != nil {
		t.Fatalf("tar close: %v", err)
	}
	return buf.Bytes()
}

// TestPullBinaryRejectsMismatchedDestination: filepath.Base(destination)
// must equal binaryName. The two extraction modes (single-file writes
// to destination, multi-file writes to destDir/binaryName) would
// otherwise disagree about where the launcher lands.
func TestPullBinaryRejectsMismatchedDestination(t *testing.T) {
	m := &Manager{logger: quietLogger()}
	dest := "/tmp/wrong/binary"
	if err := m.PullBinary("ghcr.io/example/nope", dest, "", "", "launcher"); err == nil {
		t.Errorf("PullBinary should reject destination with mismatched basename")
	}
}

// TestExtractFromTarSingleFile: legacy single-file path
func TestExtractFromTarSingleFile(t *testing.T) {
	body := "#!/bin/sh\necho hi\n"
	buf := buildTar(t, []struct {
		name string
		mode int64
		body string
	}{
		{"bin/launcher", 0755, body},
	})

	tmp := t.TempDir()
	dest := filepath.Join(tmp, "launcher")
	if err := os.MkdirAll(filepath.Dir(dest), 0755); err != nil {
		t.Fatalf("MkdirAll: %v", err)
	}

	m := &Manager{logger: quietLogger()}
	layout, err := m.extractFromTar(bytes.NewReader(buf), dest, "launcher")
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
	if !bytes.Equal(got, []byte(body)) {
		t.Errorf("body mismatch: got %q, want %q", got, body)
	}
	info, err := os.Stat(dest)
	if err != nil {
		t.Fatalf("Stat: %v", err)
	}
	if info.Mode()&0o111 == 0 {
		t.Errorf("launcher not executable: mode = %o", info.Mode())
	}
}

// TestExtractFromTarMultiFile: new multi-file path. Launcher + 2
// siblings in the same dir are written directly into destDir (no
// RemoveAll, no swap). Launcher lands at destDir/llama-server, libs
// at destDir/lib*.  The other-directory file (etc/passwd) is NOT
// extracted. destDir is preserved with its old contents intact.
func TestExtractFromTarMultiFile(t *testing.T) {
	launcherBody := "#!/bin/sh\necho launcher\n"
	lib1Body := "libllama-server-impl.so bytes\n"
	lib2Body := "libggml-cuda.so.0.13.1 bytes\n"
	otherBody := "noise\n"

	buf := buildTar(t, []struct {
		name string
		mode int64
		body string
	}{
		{"llama/llama-server", 0755, launcherBody},
		{"llama/libllama-server-impl.so", 0644, lib1Body},
		{"llama/libggml-cuda.so.0.13.1", 0644, lib2Body},
		{"etc/passwd", 0644, otherBody},
	})

	tmp := t.TempDir()
	dest := filepath.Join(tmp, "bin", "llama-server")
	if err := os.MkdirAll(filepath.Dir(dest), 0755); err != nil {
		t.Fatalf("MkdirAll: %v", err)
	}

	preExisting := filepath.Join(filepath.Dir(dest), "preexisting.conf")
	if err := os.WriteFile(preExisting, []byte("old data\n"), 0644); err != nil {
		t.Fatalf("seed: %v", err)
	}

	m := &Manager{logger: quietLogger()}
	layout, err := m.extractFromTar(bytes.NewReader(buf), dest, "llama-server")
	if err != nil {
		t.Fatalf("extractFromTar: %v", err)
	}
	if layout != "multi:llama" {
		t.Errorf("layout = %q, want %q", layout, "multi:llama")
	}

	destDir := filepath.Dir(dest)
	want := map[string][]byte{
		"llama-server":            []byte(launcherBody),
		"libllama-server-impl.so": []byte(lib1Body),
		"libggml-cuda.so.0.13.1":   []byte(lib2Body),
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

	got, err := os.ReadFile(preExisting)
	if err != nil {
		t.Errorf("preExisting gone after multi-file extract: ReadFile: %v", err)
	}
	if !bytes.Equal(got, []byte("old data\n")) {
		t.Errorf("preExisting content changed: got %q", got)
	}

	info, err := os.Stat(dest)
	if err != nil {
		t.Fatalf("Stat: %v", err)
	}
	if info.Mode()&0o111 == 0 {
		t.Errorf("launcher not executable: mode = %o", info.Mode())
	}

	libInfo, err := os.Stat(filepath.Join(destDir, "libllama-server-impl.so"))
	if err != nil {
		t.Fatalf("Stat lib: %v", err)
	}
	if libInfo.Mode().Perm() != 0644 {
		t.Errorf("lib mode = %o, want 0644", libInfo.Mode().Perm())
	}
}

// TestExtractFromTarMissing: no-match returns "" + nil.
func TestExtractFromTarMissing(t *testing.T) {
	buf := buildTar(t, []struct {
		name string
		mode int64
		body string
	}{
		{"some/other-file", 0644, "hello"},
	})

	m := &Manager{logger: quietLogger()}
	layout, err := m.extractFromTar(bytes.NewReader(buf), "/tmp/should/not/exist/launcher", "launcher")
	if err != nil {
		t.Fatalf("extractFromTar: %v", err)
	}
	if layout != "" {
		t.Errorf("layout = %q, want empty", layout)
	}
}

// TestWriteFileAtomicSetsMode: the file's final mode matches the
// requested mode (across the temp-file + rename sequence).
func TestWriteFileAtomicSetsMode(t *testing.T) {
	tmp := t.TempDir()
	path := filepath.Join(tmp, "test.txt")
	if err := writeFileAtomic(path, []byte("hello"), 0640); err != nil {
		t.Fatalf("writeFileAtomic: %v", err)
	}
	info, err := os.Stat(path)
	if err != nil {
		t.Fatalf("Stat: %v", err)
	}
	if got := info.Mode().Perm(); got != 0640 {
		t.Errorf("mode = %o, want 0640", got)
	}
}
