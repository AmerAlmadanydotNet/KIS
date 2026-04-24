"""Tests for kis.archive (create / extract / list_contents)."""

import os
import struct
from pathlib import Path

import pytest
from cryptography.exceptions import InvalidTag

from kis.archive import (
    MAGIC,
    VERSION,
    HEADER_SIZE,
    SALT_SIZE,
    NONCE_SIZE,
    create,
    extract,
    list_contents,
)


PASSWORD = "hunter2"


# ── Fixtures ──────────────────────────────────────────────────────────────────


@pytest.fixture()
def source_dir(tmp_path):
    """A small directory tree of test files."""
    root = tmp_path / "src"
    root.mkdir()
    (root / "hello.txt").write_text("Hello, World!\n")
    (root / "data.bin").write_bytes(bytes(range(256)) * 4)
    sub = root / "subdir"
    sub.mkdir()
    (sub / "nested.txt").write_text("Nested file.\n")
    return root


@pytest.fixture()
def single_file(tmp_path):
    f = tmp_path / "single.txt"
    f.write_text("Just one file.\n")
    return f


@pytest.fixture()
def archive(tmp_path, source_dir):
    path = tmp_path / "test.kis"
    create(path, [source_dir], PASSWORD)
    return path


# ── Header / format tests ─────────────────────────────────────────────────────


class TestArchiveFormat:
    def test_magic_bytes(self, archive):
        data = archive.read_bytes()
        assert data[:4] == MAGIC

    def test_version_byte(self, archive):
        data = archive.read_bytes()
        assert data[4] == VERSION

    def test_header_size(self, archive):
        expected = len(MAGIC) + 1 + SALT_SIZE + NONCE_SIZE
        assert HEADER_SIZE == expected

    def test_file_is_at_least_header_plus_tag(self, archive):
        # Minimum: header (49) + GCM tag (16) for empty ciphertext
        assert archive.stat().st_size >= HEADER_SIZE + 16

    def test_filenames_not_in_ciphertext(self, archive):
        raw = archive.read_bytes()[HEADER_SIZE:]  # skip plaintext header
        assert b"hello.txt" not in raw
        assert b"nested.txt" not in raw
        assert b"data.bin" not in raw


# ── create() ─────────────────────────────────────────────────────────────────


class TestCreate:
    def test_creates_file(self, tmp_path, source_dir):
        out = tmp_path / "out.kis"
        create(out, [source_dir], PASSWORD)
        assert out.exists()

    def test_single_file(self, tmp_path, single_file):
        out = tmp_path / "single.kis"
        create(out, [single_file], PASSWORD)
        assert out.exists()

    def test_empty_directory_raises(self, tmp_path):
        empty_dir = tmp_path / "empty_dir"
        empty_dir.mkdir()
        out = tmp_path / "empty.kis"
        with pytest.raises(ValueError, match="No files"):
            create(out, [empty_dir], PASSWORD)

    def test_missing_source_raises(self, tmp_path):
        out = tmp_path / "bad.kis"
        with pytest.raises(FileNotFoundError):
            create(out, [tmp_path / "ghost.txt"], PASSWORD)

    def test_different_passwords_produce_different_archives(self, tmp_path, source_dir):
        a1 = tmp_path / "a1.kis"
        a2 = tmp_path / "a2.kis"
        create(a1, [source_dir], "password1")
        create(a2, [source_dir], "password2")
        # Same content, different salt/nonce/ciphertext
        assert a1.read_bytes() != a2.read_bytes()


# ── list_contents() ───────────────────────────────────────────────────────────


class TestListContents:
    def test_returns_three_entries(self, archive):
        entries = list_contents(archive, PASSWORD)
        assert len(entries) == 3

    def test_entry_keys(self, archive):
        for e in list_contents(archive, PASSWORD):
            assert {"name", "size", "compressed_size", "offset"} <= e.keys()

    def test_entry_names(self, archive):
        names = {e["name"] for e in list_contents(archive, PASSWORD)}
        assert "hello.txt" in names
        assert "data.bin" in names
        assert "subdir/nested.txt" in names

    def test_sizes_correct(self, archive, source_dir):
        entries = {e["name"]: e for e in list_contents(archive, PASSWORD)}
        assert entries["hello.txt"]["size"] == (source_dir / "hello.txt").stat().st_size

    def test_wrong_password_raises(self, archive):
        with pytest.raises(InvalidTag):
            list_contents(archive, "wrong-password")

    def test_not_a_kis_file_raises(self, tmp_path):
        fake = tmp_path / "fake.kis"
        fake.write_bytes(b"NOPE" + b"\x00" * 100)
        with pytest.raises(ValueError, match="magic"):
            list_contents(fake, PASSWORD)


# ── extract() ─────────────────────────────────────────────────────────────────


class TestExtract:
    def test_round_trip_content(self, tmp_path, source_dir, archive):
        out = tmp_path / "out"
        extract(archive, PASSWORD, output_dir=out)
        assert (out / "hello.txt").read_text() == "Hello, World!\n"
        assert (out / "data.bin").read_bytes() == bytes(range(256)) * 4
        assert (out / "subdir" / "nested.txt").read_text() == "Nested file.\n"

    def test_returns_file_names(self, tmp_path, archive):
        out = tmp_path / "out"
        names = extract(archive, PASSWORD, output_dir=out)
        assert set(names) == {"hello.txt", "data.bin", "subdir/nested.txt"}

    def test_wrong_password_raises(self, tmp_path, archive):
        with pytest.raises(InvalidTag):
            extract(archive, "wrong", output_dir=tmp_path / "out")

    def test_creates_output_dir(self, tmp_path, archive):
        out = tmp_path / "does" / "not" / "exist"
        assert not out.exists()
        extract(archive, PASSWORD, output_dir=out)
        assert out.exists()

    def test_single_file_round_trip(self, tmp_path, single_file):
        arch = tmp_path / "s.kis"
        create(arch, [single_file], PASSWORD)
        out = tmp_path / "extracted"
        extract(arch, PASSWORD, output_dir=out)
        result = list(out.rglob("*"))
        assert len(result) == 1
        assert result[0].read_text() == "Just one file.\n"

    def test_compression_reduces_size(self, tmp_path):
        """Compressible data should produce a smaller compressed_size."""
        f = tmp_path / "big.txt"
        f.write_bytes(b"AAAAAAAAAA" * 10_000)
        arch = tmp_path / "big.kis"
        create(arch, [f], PASSWORD)
        entries = list_contents(arch, PASSWORD)
        assert entries[0]["compressed_size"] < entries[0]["size"]


# ── CLI integration smoke test ─────────────────────────────────────────────────


class TestCLI:
    def test_cli_create_extract(self, tmp_path, source_dir):
        import subprocess, sys
        arch = tmp_path / "cli.kis"
        out = tmp_path / "cli_out"
        env = {**os.environ, "KIS_PASSWORD": PASSWORD}
        subprocess.run(
            [sys.executable, "-m", "kis.cli", "create", str(arch), str(source_dir)],
            check=True, env=env,
        )
        subprocess.run(
            [sys.executable, "-m", "kis.cli", "extract", str(arch), "-d", str(out)],
            check=True, env=env,
        )
        assert (out / "hello.txt").read_text() == "Hello, World!\n"

    def test_cli_list(self, tmp_path, source_dir):
        import subprocess, sys
        arch = tmp_path / "cli.kis"
        env = {**os.environ, "KIS_PASSWORD": PASSWORD}
        subprocess.run(
            [sys.executable, "-m", "kis.cli", "create", str(arch), str(source_dir)],
            check=True, env=env,
        )
        result = subprocess.run(
            [sys.executable, "-m", "kis.cli", "list", str(arch)],
            capture_output=True, text=True, env=env,
        )
        assert result.returncode == 0
        assert "hello.txt" in result.stdout
