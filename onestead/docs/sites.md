# Static application publishing in Onestead

Onestead can publish a registered **网页** application at
`https://<slug>.example.com`. This first version serves static HTML, CSS,
JavaScript, images, fonts and data. It does not run a backend, start a process,
proxy a local application port, or provide an SPA fallback.

## Enable publishing

In your existing Onestead configuration, set:

```toml
public_host = "base.example.com"
site_domain = "example.com"
reserved_slugs = ["support", "status"] # optional additions
```

`ONESTEAD_SITE_DOMAIN` overrides `site_domain`. Omit it (or use an empty
environment override) to disable publishing: local MCP reports that publishing
is disabled, the UI hides its publishing controls, and application hosts return
plain-text 404 responses.

The existing `data_dir`, `owner_key` and optional `state_dir` settings still
apply. The package and commands retain their existing names, including
`onestead-mcp` and `onestead-remote`.

Keep `public_host` separate from application names. Built-in reserved names are
`www mail api admin auth login base app apps static cdn mcp sites`, plus the first
label of `public_host`. Slugs use 3–30 lowercase ASCII letters, digits or hyphens,
with no leading or trailing hyphen. Existing application slugs cannot be changed
through MCP; the owner changes them in the local UI.

## Publish and approve

1. In the local application gallery, select **发布一个网页应用**, or open an
   application's details and select **发布 / 更新到网页**. Copy the displayed
   instructions to an agent on this computer. These buttons only display text;
   they do not send a publishing request.
2. The agent builds a directory with a root `index.html`, registers the
   application with `register_app` if needed, then calls local stdio MCP:

   ```text
   publish_site(app_id="demo-web", build_dir="C:/build/demo-web", slug="demo")
   ```

   A shared agent identity must supply its permitted `signed_as` name. The agent
   needs the same application visibility and write permissions as `update_app`.
   Neither `/mcp` nor `/chatgpt/mcp` exposes this tool.
3. The first upload is **待发布**. In the local application's **网页发布** section,
   the owner chooses **指定的人**, **所有远程同事**, or **公开**, then selects **发布**.
   The named list contains enabled remote human members, not agents. Public
   access requires a second confirmation and requires no login.
4. Rebuild and call `publish_site` again to update a live site. An upload to a
   disabled site remains pending until the owner enables/publishes it.

The owner can change the audience, select a retained version to roll back,
disable/re-enable the site, or rename its slug. Renaming invalidates the old
address immediately. Changes are individual data-repository Git commits authored
by the agent or owner. The history UI can undo them; serving always follows the
current application document.

The owner may select **在手机上打开** to generate a single-use link valid for ten
minutes. Redeeming it creates a seven-day session for that site only, recorded as
**主人预览**. It does not log the owner into the public Onestead workspace. New
preview links do not invalidate earlier previews; undoing a link's issuance or
disabling the site makes the corresponding preview unusable.

## Storage and validation

Application metadata is stored in `apps/<id>.md` under `site`. File payloads are
outside Git at `<state_dir>/sites/<upload-time-slug>/<version>/`. Each version
records its immutable `storage_slug`; renaming changes the public address, not
the stored files. This allows undoing a rename without duplicating versions or
retaining obsolete copies. A version identifier contains Taipei time and a short
content hash. Only the latest five versions are retained for each application.
Deleting an old version checks real-path containment and rejects symbolic links
and directory junctions, including ancestors and nested entries.

A Git undo cannot recreate a version already removed by the five-version
retention policy. Such a missing version returns 404 rather than another site's
files or a newer version. Build it again if needed. Include the state directory
in your own backup policy if you need to retain file payloads longer than five
versions; metadata-only Git history is not a payload backup.

Uploads skip dot-prefixed files/directories and report those skipped paths.
Links/junctions are rejected, as are unapproved extensions, more than 2,000 files,
more than 50 MiB total, or a file larger than 10 MiB. Text files are scanned for
the specific credential formats in `textutil.SECRET_PATTERNS` (excluding the final
generic assignment pattern, which would reject normal minified JavaScript).
Failures report file names and line numbers, never credential values. These
patterns are a safeguard, not a guarantee that a build contains no private data.
Files are validated in a temporary directory and renamed into a complete version
before the application document is committed.

## Cloudflare Tunnel and DNS

Keep the existing public workspace hostname and its tunnel rule. Add one more
published application/public hostname to the same tunnel:

- Subdomain: `*`
- Domain: `example.com`
- Service type: **HTTP**
- Service URL: `127.0.0.1:8765`

For a locally managed tunnel, the equivalent ingress pattern is:

```yaml
ingress:
  - hostname: base.example.com
    service: http://127.0.0.1:8765
  - hostname: "*.example.com"
    service: http://127.0.0.1:8765
  - service: http_status:404
```

In the zone's DNS settings, add a **proxied CNAME** named `*` pointing to
`<tunnel-id>.cfargotunnel.com`, using the existing tunnel's ID. Keep the existing
`base` record. If the dashboard already created the wildcard DNS record, do not
add a duplicate.

Preserve the original Host header. Do **not** configure an HTTP Host Header
rewrite to `base.example.com` or `127.0.0.1`; that would erase the hostname needed
for isolation. Do not override the application's private/no-store cache headers
with a "cache everything" rule.

Cloudflare's free Universal SSL certificate normally covers `example.com` and
`*.example.com`, not `*.*.example.com`. With that certificate, use
`site_domain = "example.com"` and one-label slugs such as `demo.example.com`.
`demo.apps.example.com` needs a different certificate arrangement.

**Deployment status: not verified.** No tunnel, DNS record, Cloudflare setting,
certificate, or production service was changed or tested against the network.

## Caddy or nginx instead of Tunnel

Use either proxy in front of the same loopback service. Provision a certificate
covering `*.example.com` first (wildcard ACME certificates require DNS validation).
The certificate paths below are placeholders, not supplied files.

Caddy, with a pre-provisioned wildcard certificate:

```caddyfile
*.example.com {
    tls /etc/tls/example.com/fullchain.pem /etc/tls/example.com/privkey.pem
    reverse_proxy 127.0.0.1:8765
}
```

Caddy preserves the incoming Host by default. Automatic wildcard issuance is
also possible with an appropriate DNS provider module and DNS challenge; provider
credentials belong in the proxy's protected configuration, never an uploaded
application or the data repository.

nginx:

```nginx
server {
    listen 443 ssl;
    server_name *.example.com;
    ssl_certificate     /etc/tls/example.com/fullchain.pem;
    ssl_certificate_key /etc/tls/example.com/privkey.pem;

    location / {
        proxy_set_header Host $http_host;
        proxy_pass http://127.0.0.1:8765;
    }
}
```

Keep any existing workspace proxy behavior you require; do not broaden MCP's
allowed hosts or allowed origins to include application subdomains. These snippets
show the wildcard routing only, not a full proxy installation.

**Caddy/nginx configuration and TLS: not verified.**

## Login redirect flow

For a non-public site:

1. Without a valid site cookie, the site sends a 302 to
   `https://base.example.com/sites/enter?slug=demo&next=<local-path>`.
2. The workspace uses its existing login (token or Feishu), preserving the return
   to `/sites/enter`. It checks the current audience and member eligibility.
3. It issues a single-use, 60-second code bound to the slug, member and source
   workspace session, then redirects to
   `https://demo.example.com/_base/callback?code=...&next=...`.
4. The callback consumes the code, checks those bindings again, sets the site
   cookie, and redirects to `next`, removing the code from the address.
5. `/_base/logout` invalidates only this site session and clears its cookie.

`next` must be a local path starting with exactly one slash. Schemes, external
hosts, backslashes, control characters and encoded variants of those escape
forms are rejected and replaced by `/`.

On every request, the server checks publication status, human membership, remote
eligibility, disabled membership, audience, and the originating workspace session
(including source-token revocation and current Feishu binding). Invalid sessions
are removed immediately. Owner previews instead check their site-specific grant
and owner identity. Public sites do not require a session.

The parser cache checks application/member file paths, timestamps, sizes and
file identities on every request, reloading documents when any change is found.
It therefore also observes uncommitted permission changes, not only Git commits.
Session and source-session records are read fresh, not permission-cached.

## Security boundary

Application subdomains are **same-site but cross-origin** relative to the workspace.
Treat uploaded scripts as untrusted with respect to the workspace:

- Host routing runs before the workspace guard. Exactly one lowercase Host matching
  `<slug>.<site_domain>` can reach a site; it can never fall through to a workspace,
  MCP, skill-download, authentication, or discovery route. Unknown hosts get a
  plain-text 404. A workspace host cannot serve application files.
- The site cookie is `__Host-cb_site`: Secure, HttpOnly, SameSite=Lax, Path=/ and no
  Domain attribute. `__Host-` prevents sibling-domain cookie injection. Lax allows
  the cookie on top-level links opened from messaging apps. The workspace ignores
  this cookie; the site ignores workspace cookies and Bearer tokens.
- Cookies and codes are random values stored only as hashes in server-side state.
  The site cookie is not a workspace login or MCP credential.
- Workspace write requests still require an **exact Origin match**, the existing
  request marker, and CSRF validation. `Sec-Fetch-Site: same-site` is not sufficient;
  a POST from an application subdomain is rejected even with a valid workspace
  cookie and CSRF token. Local owner actions retain the desktop guard's exact
  loopback Origin and request-marker checks.
- Static paths are decoded and checked before normalization. Dot-prefixed segments,
  traversal, backslashes, NUL/control characters and Windows path aliases are
  rejected. Directory requests use `index.html`; missing files return 404.
- Content types are explicit. Responses use `X-Content-Type-Options: nosniff`,
  `Referrer-Policy: same-origin` and CSP `frame-ancestors 'self'`. HTML is
  `private, no-cache`; other static files are `private, max-age=60`. Application
  script sources are intentionally not restricted by Onestead.

The repository's `test_sites*` tests exercise these boundaries using temporary
repositories and state. Real phone browsers, external Feishu authorization,
Cloudflare edge behavior and Internet reachability are **not verified**.
