"""
Command-line interface for KIS archives.

Usage
-----
  kis create  <archive.kis> <file|dir> [<file|dir> ...]
  kis extract <archive.kis> [-d OUTPUT_DIR]
  kis list    <archive.kis>

The password is read from the ``KIS_PASSWORD`` environment variable when set,
otherwise prompted interactively (without echo).
"""

import argparse
import getpass
import os
import sys

from cryptography.exceptions import InvalidTag

from .archive import create, extract, list_contents


def _get_password(args: argparse.Namespace) -> str:
    env_pw = os.environ.get("KIS_PASSWORD")
    if env_pw is not None:
        return env_pw
    if hasattr(args, "password") and args.password:
        return args.password
    return getpass.getpass("Password: ")


def _cmd_create(args: argparse.Namespace) -> int:
    password = _get_password(args)
    try:
        create(args.archive, args.sources, password)
        print(f"Archive created: {args.archive}")
        return 0
    except (FileNotFoundError, ValueError) as exc:
        print(f"Error: {exc}", file=sys.stderr)
        return 1


def _cmd_extract(args: argparse.Namespace) -> int:
    password = _get_password(args)
    try:
        extracted = extract(args.archive, password, output_dir=args.output_dir)
        for name in extracted:
            print(f"  extracted: {name}")
        print(f"{len(extracted)} file(s) extracted to '{args.output_dir}'.")
        return 0
    except InvalidTag:
        print("Error: Wrong password or archive is corrupt.", file=sys.stderr)
        return 1
    except (ValueError, OSError) as exc:
        print(f"Error: {exc}", file=sys.stderr)
        return 1


def _cmd_list(args: argparse.Namespace) -> int:
    password = _get_password(args)
    try:
        entries = list_contents(args.archive, password)
        if not entries:
            print("(empty archive)")
            return 0
        name_w = max(len(e["name"]) for e in entries)
        print(f"{'Name':<{name_w}}  {'Original':>12}  {'Compressed':>12}  {'Ratio':>6}")
        print("-" * (name_w + 36))
        for e in entries:
            ratio = e["compressed_size"] / e["size"] if e["size"] else 1.0
            print(
                f"{e['name']:<{name_w}}  {e['size']:>12,}  "
                f"{e['compressed_size']:>12,}  {ratio:>5.1%}"
            )
        total_orig = sum(e["size"] for e in entries)
        total_comp = sum(e["compressed_size"] for e in entries)
        overall = total_comp / total_orig if total_orig else 1.0
        print("-" * (name_w + 36))
        print(
            f"{'Total':<{name_w}}  {total_orig:>12,}  {total_comp:>12,}  {overall:>5.1%}"
        )
        return 0
    except InvalidTag:
        print("Error: Wrong password or archive is corrupt.", file=sys.stderr)
        return 1
    except (ValueError, OSError) as exc:
        print(f"Error: {exc}", file=sys.stderr)
        return 1


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="kis",
        description="KIS — Keep It Simple archive tool (LZMA + AES-256-GCM, encrypted filenames)",
    )
    parser.add_argument(
        "--password", "-p",
        metavar="PASSWORD",
        help="Archive password (prefer KIS_PASSWORD env var or interactive prompt).",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    # create
    p_create = sub.add_parser("create", aliases=["c"], help="Create a new KIS archive.")
    p_create.add_argument("archive", help="Destination .kis file.")
    p_create.add_argument("sources", nargs="+", metavar="source",
                          help="Files or directories to add.")
    p_create.set_defaults(func=_cmd_create)

    # extract
    p_extract = sub.add_parser("extract", aliases=["x", "e"], help="Extract a KIS archive.")
    p_extract.add_argument("archive", help="Source .kis file.")
    p_extract.add_argument("-d", "--output-dir", default=".", metavar="DIR",
                           help="Directory to extract files into (default: current dir).")
    p_extract.set_defaults(func=_cmd_extract)

    # list
    p_list = sub.add_parser("list", aliases=["l"], help="List archive contents.")
    p_list.add_argument("archive", help="Source .kis file.")
    p_list.set_defaults(func=_cmd_list)

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
