/**
 * Single source of truth for the admin SPA's "Open Web UI" links that
 * point at each workstation agent's locally-served tray UI.
 *
 * Background
 * ----------
 * The PRISM agent (v0.1.31+) serves its tray UI on
 * `http://<host>:7421/` by default (`webUiPort` + `webUiBindAll`).
 *
 * Three sources of `<host>`, in precedence order:
 *
 *   1. `host` — the live `agent_sessions.remote_addr` for the
 *      currently-connected agent, surfaced through `/api/workstations`.
 *      Always preferred when present because Chrome's HTTPS-First Mode
 *      (and any HSTS `includeSubDomains` policy on the parent zone,
 *      e.g. `rebus.industries`) silently upgrades `http://<name>.<zone>`
 *      to `https://`, which the agent's plain-HTTP listener does not
 *      serve. **Bare IPs are not upgraded by Chrome's HTTPS-upgrade
 *      logic** (see https://chromestatus.com/feature/6378571769184256),
 *      so an IP-based URL clicks through cleanly. Also works on flat
 *      LANs that don't have AD DNS to resolve nodeName.dnsSuffix.
 *
 *   2. `dnsSuffix` — the legacy fallback. When the `workstation_dns_suffix`
 *      admin setting is configured (e.g. `ad.rebus.industries`), the
 *      suffix is appended to `nodeName`. Only used when `host` is null
 *      (i.e. the agent is currently offline so the server can't surface
 *      a live IP). HTTPS-upgrade caveats apply if the suffix is under
 *      an HSTS-enabled zone.
 *
 *   3. `nodeName` alone — final fallback. Works on AD-joined VLANs
 *      where `DC1` resolves bare hostnames; otherwise the link will
 *      404 in the browser and the operator has to wait for the agent
 *      to reconnect.
 *
 * Keep this dead simple: string concatenation, no URL constructor,
 * no protocol negotiation -- just match the literal format the agent
 * binds to.
 */

/** Default port the agent's local web UI listens on (`webUiPort` in
 *  PRISM.Agent's `AgentConfig`). Matches every install we control;
 *  if/when the port becomes per-workstation configurable we'll need
 *  to surface it through `/api/workstations` and thread it through
 *  these helpers. */
export const AGENT_WEB_UI_PORT = 7421;

/**
 * Resolve the hostname the admin browser should use to reach the
 * agent's local web UI for a given workstation.
 *
 * @param nodeName    The `nodeName` column from the workstations table.
 *                    Trimmed defensively; never empty in practice but
 *                    guarded so the URL stays well-formed.
 * @param dnsSuffix   The `workstation_dns_suffix` admin setting. Used
 *                    only when `host` is unset. Any leading `.` on the
 *                    suffix is stripped here so callers don't have to
 *                    worry about double dots.
 * @param host        Optional live IP for the connected agent, from
 *                    `Workstation.host` (`agent_sessions.remote_addr`).
 *                    When set, returned verbatim — it's already an IP
 *                    so we deliberately do NOT append `dnsSuffix`.
 */
export function workstationWebUiHost(nodeName: string, dnsSuffix: string, host?: string | null): string {
  const liveHost = (host ?? '').trim();
  if (liveHost) return liveHost;
  const name = (nodeName ?? '').trim();
  const suffix = (dnsSuffix ?? '').trim().replace(/^\.+/, '');
  return suffix ? `${name}.${suffix}` : name;
}

/**
 * Build the full `http://...:7421/` URL for the agent's local web UI.
 * Mirrors `workstationWebUiHost` and tacks on the port + scheme.
 */
export function workstationWebUiUrl(nodeName: string, dnsSuffix: string, host?: string | null): string {
  return `http://${workstationWebUiHost(nodeName, dnsSuffix, host)}:${AGENT_WEB_UI_PORT}/`;
}
