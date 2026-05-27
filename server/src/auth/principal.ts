/**
 * Request principal — the resolved identity attached to a Fastify request
 * after the auth middleware runs. Route handlers read `request.principal`.
 *
 * Three flavours:
 *   - apiKey: external `/v1/*` caller with X-API-Key header
 *   - orbitUser: an ORBIT bearer token validated against orbit-server
 *   - adminSession: a logged-in admin (signed cookie)
 *
 * `apiKey` principals carry the row's `scopes` array. `requireScope(scope)`
 * (server/src/auth/middleware.ts) treats an empty list as "legacy unscoped"
 * — i.e. the key keeps the historical "any /v1/*" surface — but new scopes
 * (e.g. `visualiser:create_stream`) are only granted to keys that
 * explicitly list them.
 */
export type Principal =
  | { kind: 'apiKey'; apiKeyId: string; apiKeyName: string; scopes: string[] }
  | { kind: 'orbitUser'; userId: string; orbitToken: string; serverUrl: string }
  | { kind: 'adminSession'; adminUserId: string; username: string };

declare module 'fastify' {
  interface FastifyRequest {
    principal?: Principal;
  }
}
