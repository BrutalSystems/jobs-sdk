# Releasing the Python SDK to PyPI

Publishing uses **Trusted Publishing** (OIDC) — GitHub Actions authenticates to
PyPI directly, so there is **no API token stored anywhere**. The workflow is
`.github/workflows/release.yml`; it fires on a `python-v*` tag.

## One-time PyPI setup (do this before the first release)

The two packages don't exist on PyPI yet, so use **pending publishers**. On
<https://pypi.org> → *Your account* → *Publishing* → *Add a pending publisher*,
add **one entry per package**:

| Field | `brutalsystems-jobs-core` | `brutalsystems-jobs-client` |
|-------|---------------------------|------------------------------|
| PyPI Project Name | `brutalsystems-jobs-core` | `brutalsystems-jobs-client` |
| Owner | `BrutalSystems` | `BrutalSystems` |
| Repository name | `jobs-sdk` | `jobs-sdk` |
| Workflow name | `release.yml` | `release.yml` |
| Environment name | **`pypi`** | **`pypi-client`** |

> **Gotcha (learned the hard way):** PyPI keys a trusted publisher on the tuple
> (owner, repo, workflow, **environment**) and refuses two publishers with the
> *identical* tuple — and it reports the conflict as a misleading "Sorry,
> something went wrong / PyPI is down" 503 page, not a clean validation error.
> That's why the two packages use **different environment names**. `release.yml`
> has a matching job per environment, so each OIDC token carries the right one.

(Optional but recommended: in GitHub repo *Settings → Environments*, create
`pypi` and `pypi-client` and add required reviewers, so a human approves each
publish.)

## Cutting a release

1. Bump `version` in **both** `python/packages/jobs-core/pyproject.toml` and
   `python/packages/jobs-client/pyproject.toml` (kept in lockstep for now).
2. Commit, then tag and push:
   ```sh
   git tag python-v0.1.0
   git push origin python-v0.1.0
   ```
3. The `Release (PyPI)` workflow builds both sdists+wheels and publishes them.

After the first successful publish, the pending publishers become regular
trusted publishers automatically.

## After publishing — switch the service off the git dependency

Once the packages are on PyPI, update the private `jobs-service` repo to depend on
the published version instead of git: drop the `[tool.uv.sources]` git entry and
pin `brutalsystems-jobs-core==<version>` (and simplify the Dockerfile's git
install to a plain `pip install`). That removes the `apt-get git` step too.
