"""Public-client Supabase round trip and two-user denial tests.

--prepare creates SQL to seed temporary fictional Auth identities through an
authorized administrator MCP call. No service key is shipped or used by this
test. Temporary login material is protected with current-user Windows DPAPI.
After --verify, run --cleanup-sql through administrator MCP to remove only these
test identities. REST data and selected Storage artifacts are deleted by users.
"""

import argparse
import asyncio
import json
import secrets
import uuid
from pathlib import Path

import httpx
from evoflow_worker.cloud import Cloud
from evoflow_worker.credentials import Vault, _crypt
from evoflow_worker.store import Store

ROOT = Path(__file__).resolve().parents[1]
HOME = ROOT / "artifacts" / "local" / "cloud-roundtrip"
FILE = HOME / "test-identities.dpapi"


async def verify(users):
    config = json.loads((ROOT / "config" / "runtime.json").read_text())
    clients = []
    checks = []

    async def check(name, passed):
        checks.append({"name": name, "passed": bool(passed)})
        print(json.dumps(checks[-1]), flush=True)
        if not passed:
            raise AssertionError(name)

    try:
        for index, user in enumerate(users):
            root = HOME / str(index)
            client = Cloud(config, Vault(root), Store(root))
            await client.sign_in(user["email"], user["password"])
            clients.append(client)
            await check(f"user_{index}_authenticated", client.identity() == user["id"])
        alice, bob = clients
        workspace = await alice.ensure_workspace()
        identifier = str(uuid.uuid4())
        record = {
            "id": identifier,
            "workspace_id": workspace,
            "owner_id": alice.identity(),
            "kind": "run",
            "payload": {"fictional_test": True, "verified_round_trip": True},
        }
        await alice.request("POST", "/rest/v1/records", json=record)
        allowed = (await alice.request("GET", f"/rest/v1/records?id=eq.{identifier}")).json()
        denied = (await bob.request("GET", f"/rest/v1/records?id=eq.{identifier}")).json()
        await check(
            "owner_can_read_written_record", len(allowed) == 1 and allowed[0]["payload"] == record["payload"]
        )
        await check("other_user_cannot_read_record", denied == [])
        blocked = False
        try:
            await bob.request(
                "POST", "/rest/v1/records", json=dict(record, id=str(uuid.uuid4()), owner_id=bob.identity())
            )
        except RuntimeError:
            blocked = True
        await check("other_user_cannot_write_workspace", blocked)
        forged = False
        try:
            await alice.request(
                "PATCH", f"/rest/v1/records?id=eq.{identifier}", json={"owner_id": bob.identity()}
            )
        except RuntimeError:
            forged = True
        await check("owner_cannot_reassign_record_to_another_user", forged)
        async with httpx.AsyncClient() as raw:
            anonymous = await raw.get(
                config["supabase_url"] + "/rest/v1/records",
                headers={"apikey": config["supabase_publishable_key"]},
            )
        await check("anonymous_read_denied", anonymous.status_code in {401, 403})
        path = await alice.upload_selected("verification.json", b'{"fictional":"selected artifact"}')
        content = await alice.request("GET", "/storage/v1/object/authenticated/evoflow-private/" + path)
        await check("private_storage_owner_download", content.json() == {"fictional": "selected artifact"})
        blocked = False
        try:
            await bob.request("GET", "/storage/v1/object/authenticated/evoflow-private/" + path)
        except RuntimeError:
            blocked = True
        await check("private_storage_other_user_denied", blocked)
        await alice.request("DELETE", "/storage/v1/object/evoflow-private", json={"prefixes": [path]})
        await alice.request("DELETE", f"/rest/v1/records?id=eq.{identifier}")
        await alice.request("DELETE", f"/rest/v1/workspaces?id=eq.{workspace}")
    finally:
        for client in clients:
            await client.sign_out()
        (HOME / "cloud-verification.json").write_text(
            json.dumps(
                {
                    "project": "csvfsltznjdaalnzvgbz",
                    "public_client_only": True,
                    "checks": checks,
                    "auth_sessions_signed_out": True,
                },
                indent=2,
            )
        )


parser = argparse.ArgumentParser()
parser.add_argument("action", choices=["prepare", "verify", "cleanup-sql"])
args = parser.parse_args()
HOME.mkdir(parents=True, exist_ok=True)
if args.action == "prepare":
    if FILE.exists():
        raise SystemExit("Existing test identities must be cleaned up first")
    users = [
        {
            "id": str(uuid.uuid4()),
            "email": f"evoflow-test-{uuid.uuid4().hex}@example.invalid",
            "password": secrets.token_urlsafe(32),
        }
        for _ in range(2)
    ]
    FILE.write_bytes(_crypt(json.dumps(users).encode(), False))
    sql = ["begin;"]
    for user in users:
        sql.append(f"""insert into auth.users(instance_id,id,aud,role,email,encrypted_password,email_confirmed_at,
          raw_app_meta_data,raw_user_meta_data,created_at,updated_at,confirmation_token,recovery_token,
          email_change_token_new,email_change,email_change_token_current,reauthentication_token)
          values('00000000-0000-0000-0000-000000000000','{user["id"]}','authenticated','authenticated',
          '{user["email"]}',extensions.crypt('{user["password"]}',extensions.gen_salt('bf')),now(),
          '{{"provider":"email","providers":["email"]}}','{{}}',now(),now(),'','','','','','');
          insert into auth.identities(id,user_id,provider_id,identity_data,provider,created_at,updated_at)
          values(gen_random_uuid(),'{user["id"]}','{user["id"]}',
          '{{"sub":"{user["id"]}","email":"{user["email"]}","email_verified":true}}','email',now(),now());""")
    sql.append("commit;")
    print("\n".join(sql))
else:
    users = json.loads(_crypt(FILE.read_bytes(), True))
    if args.action == "verify":
        asyncio.run(verify(users))
    else:
        ids = ",".join("'" + user["id"] + "'" for user in users)
        print(f"delete from auth.users where id in ({ids}) and email like 'evoflow-test-%@example.invalid';")
