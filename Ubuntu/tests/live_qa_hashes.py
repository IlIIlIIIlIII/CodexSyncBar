#!/usr/bin/env python3
"""Export hash-only canonical identities through the running service's vault reader.

No credential decryption happens in this script and no tokens, account IDs or email
addresses are returned by the endpoint. Run after all canonical accounts are imported,
then give the resulting private JSON to live_qa.py --expected.
"""
import argparse
import json
import os
from pathlib import Path
import re
import stat
import sys
import uuid
from live_qa import Failure, rpc


def validated_export(result):
    exported = {}
    for profile in result.get("profiles", []):
        identifier = profile.get("profileId")
        if type(identifier) is not int or identifier <= 0 or str(identifier) in exported:
            raise Failure("invalid_canonical_profile")
        entry = {key: profile.get(key) for key in ("accountIdSha256", "emailSha256")}
        if any(not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{64}", value) for value in entry.values()):
            raise Failure("invalid_canonical_identity_hash")
        exported[str(identifier)] = entry
    if len({entry["accountIdSha256"] for entry in exported.values()}) < 2:
        raise Failure("two_registered_distinct_accounts_required")
    home_hash = result.get("codexHomeSha256")
    if not isinstance(home_hash, str) or not re.fullmatch(r"[0-9a-f]{64}", home_hash):
        raise Failure("invalid_canonical_home_hash")
    exported["_metadata"] = {"source": "encrypted-canonical-vault", "endpoint": "account.identityHashes",
        "codexHomeSha256": home_hash, "configurationRevision": result.get("configurationRevision")}
    return exported


def export(socket_path, output_path):
    info = Path(socket_path).lstat()
    if not stat.S_ISSOCK(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o077:
        raise Failure("unsafe_service_socket")
    snapshot = rpc(socket_path, "snapshot")
    if snapshot.get("isDemo") or snapshot.get("isBusy") or snapshot.get("error"):
        raise Failure("production_service_not_ready")
    document = validated_export(rpc(socket_path, "account.identityHashes"))
    output = Path(output_path).absolute()
    output.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    temporary = output.with_name(output.name + "." + uuid.uuid4().hex)
    try:
        with os.fdopen(os.open(temporary, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600), "w") as stream:
            json.dump(document, stream, indent=2)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, output)
    finally:
        temporary.unlink(missing_ok=True)
    return {"ok": True, "exportedProfiles": sorted(int(key) for key in document if key != "_metadata"),
            "source": "encrypted-canonical-vault", "file": str(output)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--socket", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    try:
        print(json.dumps(export(args.socket, args.output)))
        return 0
    except BaseException as error:
        print(json.dumps({"ok": False, "error": str(error) if isinstance(error, Failure) else "canonical_export_failed"}))
        return 1


if __name__ == "__main__":
    sys.exit(main())
