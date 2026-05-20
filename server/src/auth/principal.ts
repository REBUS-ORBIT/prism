/**
 * Request principal — the resolved identity attached to a Fastify request
 * after the auth middleware runs. Route handlers read `request.principal`.
 *
 * Three flavours:
 *   - apiKey: external `/v1/*` caller with X-API-Key header
 *   - orbitUser: an ORBIT bearer token validated against orbit-server
 *   - adminSession: a logged-in admin (signed cookie)
 */
export type Principal =
  | { kind: 'apiKey'; apiKeyId: string; apiKeyName: string }
  | { kind: 'orbitUser'; userId: string; orbitToken: string; serverUrl: string }
  | { kind: 'adminSession'; adminUserId: string; username: string };

declare module 'fastify' {
  interface FastifyRequest {
    principal?: Principal;
  }
}
