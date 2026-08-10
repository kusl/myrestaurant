# M6 Slice 26 — the stack that would not start anywhere else

Two findings, one new script, one behavioural fix to `compose.yaml`, and the documentation that has to
move with them. No C#, no Razor, no migration, no test. **Test count is unchanged at 1056.**

Extract this archive at the repository root. Every file in it is a complete file at its exact
repo-relative path.

---

## Files in this archive

| Path | New? | Why |
|---|---|---|
| `scripts/dev_instance.sh` | **new** | The script this slice is for. Detached demo instance on a host with no .NET SDK. |
| `compose.yaml` | changed | F-51: fully-qualified image references. Plus `SOURCE_REVISION` as a build arg. |
| `.env.example` | changed | Documents the new script's knobs, and warns against pinning `RESTAURANT_PUBLIC_ORIGIN`. |
| `README.md` | changed | F-52: corrects the "cannot print a URL and exit" claim; adds the new script to the inventory and to Prerequisites. |
| `docs/TECHNICAL_SPECIFICATION.md` | changed | §14.1 image rule; new §14.3a; Appendix A F-51/F-52; header **v1.11** and matching changelog entry. |
| `docs/OPERATIONS.md` | changed | §1 profile table gains a third column; §10 corrected; new §10a runbook. |
| `docs/adr/0005-origins-and-tls-cloudflare-named-tunnel.md` | changed | New point 7: tunnel lifetime is a property of ownership, not of tunnels. |
| `docs/DOCUMENTATION_REVIEW.md` | changed | F-51 and F-52 ledger rows, plus the closing narrative. |
| `docs/BUILD_PROGRESS.md` | changed | Slice 26 section appended. Ships whole, as every archive does now. |
| `_CHANGES.md` | changed | This file. |

## Files to DELETE

**None.** Nothing in the tree is superseded by this slice. In particular
`scripts/quick_tunnel.sh` **stays** — see the ruling below.

## After extracting

```bash
git add scripts/dev_instance.sh
```

Required. `scripts/check_tree.sh` and the CI `shell-scripts` job both enumerate with
`git ls-files`, so an unadded script is a script no gate looks at.

---

## The two findings

### F-51 — the canonical stack could not start on a stock Debian

`compose.yaml` named `postgres:17-alpine` and `caddy:2-alpine` by short name. A short name is not an
image reference, it is a query resolved through `unqualified-search-registries` in
`/etc/containers/registries.conf` — which Fedora's `containers-common` populates and a stock Debian
ships commented out. On Debian, rootless Podman answers `podman-compose up` with:

```
Error: short-name "postgres:17-alpine" did not resolve to an alias and no
       unqualified-search registries are defined in "/etc/containers/registries.conf"
Error: no container with name or ID "myrestaurant_postgres_1" found: no such container
Error: "myrestaurant_postgres_1" is not a valid container, cannot be used as a dependency
```

Errors two and three are consequences of error one, and none of the three names `compose.yaml`.

**Nothing in this repository could have caught it.** `check_tree.sh` reads tracked text and this file
is correct as text. `ConfigurationSurfaceTests` audits this exact file — the `environment` mapping,
four lines below the `image:` line. CI runs on Ubuntu with Docker, which resolves short names. And no
test starts `compose.yaml` at all: the Testcontainers fixtures build their own container
specification, boot-smoke boots the image with an environment the workflow supplies.

**The rule already existed, applied to one place.** `scripts/restore_drill.sh` has defaulted
`DRILL_POSTGRES_IMAGE` to `docker.io/library/postgres:17-alpine` since Slice 16, with the reason
written beside it. Somebody solved this once, for a scratch container in a rehearsal, and left the
stack being rehearsed alone.

### F-52 — two documents explained why something was impossible

`README.md` said a quick tunnel *"cannot 'print a URL and exit', because exiting kills the URL"*.
OPERATIONS §10 said *"there is no detached mode … because the tunnel dies with the process that owns
it"* — a sentence containing its own refutation. The tunnel does die with its owner, and
`quick_tunnel.sh` **is** the owner, because it runs `cloudflared` as a foreground child and blocks on
it. Ownership is a choice: as a detached container, the engine owns it and the shell can exit.

The cost was the closed door. The case that needed it — a spare LAN machine over SSH, no .NET SDK,
serving testers for days — had been documented as impossible for four milestones.

---

## Decisions, and how to reverse each one

### 1. A second script rather than a flag on `quick_tunnel.sh`

`quick_tunnel.sh` is unchanged and stays. Its foreground shape is correct for a demo somebody is
standing in front of: the URL dies with the terminal, which is the safe default for a throwaway. The
detached shape has an obligation attached — `down` is the only thing that stops it — and folding both
into one script with a `--detach` flag means one of the two behaviours is a surprise. Two scripts, two
names, two runbooks (§14.3 and §14.3a).

*Reverse:* delete `scripts/dev_instance.sh`. Nothing else depends on it.

### 2. Fully-qualified image references — **required, not optional**

`docker.io/library/postgres:17-alpine` and `docker.io/library/caddy:2-alpine`. Valid on Docker
(`docker.io/library/…` is the canonical long form of a Hub library image), required on Debian Podman.
**The new script cannot work without this**, because it is `podman-compose up` underneath.

*Reverse:* delete the two `docker.io/library/` prefixes, and accept that the canonical stack does not
start on a stock Debian.

### 3. `SOURCE_REVISION` as a compose build argument — optional

```yaml
      args:
        SOURCE_REVISION: ${SOURCE_REVISION:-}
```

`dev_instance.sh` sets it from `git rev-parse HEAD`, so `/source` names the commit a tester actually
reached instead of "not recorded" — worth having when bug reports arrive from testers.

**Empty default, on F-50's ruling.** `Containerfile`'s own `ARG SOURCE_REVISION=` is the one place the
fallback lives, and it renders an unstamped build as *not recorded* rather than guessing. Repeating a
value here would override the one place the default is written down, which is F-50 one layer up.

**Placement is load-bearing and was proven, not assumed.** `ConfigurationSurfaceTests` reads the `web`
service's environment keys by indentation, and `      args:` is a six-space key — the same depth as an
environment key. It sits **above** `environment:`, so it falls outside the scanned span. Verified by
porting the scan and running it against the delivered file (twenty keys, `SOURCE_REVISION` absent), and
proven sensitive by moving the same block below `environment:` (twenty-one keys, `SOURCE_REVISION`
present, F-50's assertion would fail).

**Failure mode if wrong is benign:** an engine that ignored `build.args` would produce an unstamped
image, not a broken one.

*Reverse:* delete the `args:` key and its two lines. The `Containerfile` `ARG` default takes over.

### 4. F-51 is deliberately **not** made executable

This declines F-38's habit — a row in the embodiment column names something that runs — for the first
time in eight applications, on F-41's reasoning. The available check is "no `image:` value lacks a
registry component": a text assertion about a file whose contract is behavioural. It would pass on a
tree where the images are qualified and the stack still cannot start for the next reason, and it would
report a finding the day somebody legitimately references a local image. What catches this class is a
CI job that runs the canonical stack on the canonical engine, which is recorded as an open item rather
than closed with a grep that resembles one.

### 5. State lives outside the repository

`${XDG_STATE_HOME:-~/.local/state}/myrestaurant/dev-instance.env`. No `.gitignore` change, nothing for
`check_tree.sh` to classify, and no untracked file appearing in a working tree an operator might
commit. The running tunnel's log is the source of truth for the URL; the file is a cache for `url`
after the tunnel is gone.

### 6. Order of operations: build → tunnel → stack

The one thing in the script that is a fix rather than a feature.

`quick_tunnel.sh` opens the tunnel and *then* builds. On the cold Debian host that produced F-51 the
public URL was printed at minute zero and the application became reachable nineteen minutes later.

Building first means the tunnel URL is in hand **before** `web` is created, so `RESTAURANT_PUBLIC_ORIGIN`
is exported and `up -d` runs once. That also avoids `up --force-recreate web`, whose behaviour is not
what its name suggests. From podman-compose 1.3.0 `compose_up`:

```python
if args.force_recreate or len(diff_hashes):
    down_args = argparse.Namespace(**dict(args.__dict__, volumes=False))
    await compose.commands["down"](compose, down_args)
```

It is a `down` of the **whole project**, then an `up` of the named service — it restarts the database
and deletes and recreates the network. Both of your terminal logs show this as an unexplained network
id appearing twice.

The engine's own recreate-on-change is relied on instead, and it is sound: `self.yaml_hash` is computed
*after* `rec_subs(content, self.environ)`, so a changed origin is a changed config hash and a later run
with a new URL recreates by itself.

### 7. A second `up` reuses the hostname

Not an optimisation. `*.trycloudflare.com` is on the Public Suffix List and the subdomain is random per
tunnel, so a tester's registered passkey is bound to that exact hostname. Restart the app, rebuild after
a `git pull`, reboot the host — the hostname holds as long as the tunnel container does. `--new-url` is
how to discard it deliberately, and it says what it is about to break.

### 8. Probing without assuming the host has an HTTP client

`curl`, then `wget`, then the `curl` the runtime image installs for its own compose healthcheck, reached
with `podman exec`. The third path is why this works on a minimal Debian: it is a client guaranteed to
exist whenever there is anything worth probing, and it reaches both the application (it *is* the
application) and the public URL (same egress as the tunnel).

### 9. Engine and compose chosen together; containers found by label

The engine is selected first and the compose command selected *for it*. Two independent `PATH` searches
can disagree on a host with both engines, leaving the stack in one store while `logs`, `exec` and `rm`
look in the other — F-43 with a different pair of commands. Containers are found with
`--filter label=com.docker.compose.service=web` rather than by guessing `<project>_web_1`; both engines
label what they create.

---

## What was verified

No .NET SDK and no container engine available, so everything below is a check that could actually run.

- `bash -n` and `shellcheck` on `scripts/dev_instance.sh` at `--severity=warning` (blocking in CI) and
  `--severity=style` (advisory). Clean at both. Baselined against the nine existing scripts first, to
  confirm the installed shellcheck agrees with CI's on this tree.
- podman-compose 1.3.0 **read, not remembered**, for the four behaviours the design rests on: `.env`
  does not beat the process environment (lines 1927–1928); `build.args` is forwarded as `--build-arg`
  (line 2516); `build` accepts a service name; `up --force-recreate` downs the whole project.
- `compose.yaml` parsed with a real YAML parser and the parsed document inspected.
- `ConfigurationSurfaceTests` ported to Python and run against the edited tree — all three restatement
  assertions pass; compose scan reports the same twenty keys with the same block boundaries. Sensitivity
  proven by planting the `args` block in the wrong place.
- `SpecificationVersionTests` ported and run: header **1.11**, entries 1.11 … 1.0 descending. Note
  `Version.TryParse` reads `1.11` as minor **eleven**, so it sorts above 1.10 — checked, because a
  string comparison gets it backwards.
- Every documentation edit applied by exact-match replacement, asserting the anchor occurs exactly once.
  Nothing edited by position.
- Byte hygiene on every delivered file: LF endings, exactly one final newline, no CR, no whitespace-only
  lines, no trailing whitespace, no context-dump separator.

## What was NOT verified

**`scripts/dev_instance.sh` has never been executed.** A parser and a linter have looked at it; nothing
else. Everything it does to a container engine is reasoned from podman-compose's source and your two
terminal logs. Expect a second pass after the first real run.

**F-51's fix has not been observed to work.** Fully-qualified names resolving without
`unqualified-search-registries` is how the registry code is documented to behave and how
`restore_drill.sh` has behaved since Slice 16 — but the machine that produced the error has not run the
corrected file.

---

## Suggested first run on the test host

```bash
cd ~/src/dotnet/myrestaurant
git pull
git add scripts/dev_instance.sh

bash scripts/dev_instance.sh --help
time bash scripts/dev_instance.sh

bash scripts/dev_instance.sh url
bash scripts/dev_instance.sh status
```

Then the thing this slice is actually about — **close the SSH session, reconnect, and check the URL
still answers.** If it does not, the linger line the script printed is where to look.

To confirm F-51 independently, before any of the above:

```bash
podman-compose config | grep image:
podman-compose up -d postgres
```

The second command is the one that produced three errors before this slice.

## Still open

- **A CI job that runs the canonical stack on the canonical engine.** F-51's real embodiment, not in
  this slice. Nothing CI does today executes `compose.yaml`.
- **`OPERATIONS.md` §2 asserts behaviour no code has** — it says `.env` is created automatically by
  `run.sh` and the scripts. All nine were grepped; none touches `.env`. That is F-38's shape aimed
  inward and deserves its own slice, because the interesting question is which side is right.
  `dev_instance.sh` warns rather than creating, which is the conservative choice while it is open.
- `Permissions-Policy`, carried forward from Slice 24.
- Two operator actions no archive can contain: private vulnerability reporting, and the repository
  description (F-42).
