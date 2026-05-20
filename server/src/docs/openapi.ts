/**
 * OpenAPI 3.1 spec for PRISM's external API.
 *
 * Scope: only the public `/v1/*` namespace. The internal `/api/*` routes
 * (admin SPA, agent uploads, etc.) are intentionally NOT documented here
 * because they're not stable for third-party use.
 *
 * Served at GET /api/openapi.json (public, no auth required so external
 * developers can read it without provisioning a key).
 */

/**
 * Build the OpenAPI document. We accept a runtime `publicBaseUrl` so the
 * generated `servers[].url` can be overridden per deployment (prod / dev).
 */
export function buildOpenApi(publicBaseUrl: string): unknown {
  const SERVER_URL = (publicBaseUrl || '').replace(/\/+$/, '') + '/v1';

  return {
    openapi: '3.1.0',
    info: {
      title: 'PRISM API',
      version: '1.0.0',
      summary: 'CAD / mesh conversion + ORBIT object delivery as a service.',
      description: [
        'PRISM accepts CAD, mesh, and IFC files via HTTP, dispatches the conversion work',
        'to a pool of Rhino workstation agents, and uploads the resulting ORBIT objects',
        'to your configured ORBIT server — preserving native B-rep / SubD / Extrusion',
        'geometry through `RhinoDataObject.rawEncoding`.',
        '',
        '## Authentication',
        '',
        'All `/v1/*` endpoints require an API key supplied in the `X-API-Key` header.',
        'Mint keys in the admin UI under **API keys** (an admin must do this for you;',
        'we do not yet self-serve key issuance). Keep keys secret — they grant the',
        'ability to spend your monthly conversion quota.',
        '',
        '## Rate limits',
        '',
        'Every request is metered against the issuing key. Two policies apply:',
        '',
        '* **Per-minute rate limit** — short-burst protection (default 60/min, configurable per key).',
        '* **Monthly quota** — total conversion + receive jobs per calendar month.',
        '',
        'Both budgets are reported via response headers:',
        '',
        '* `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset`',
        '* `X-Quota-Limit`, `X-Quota-Remaining`, `X-Quota-Reset`',
        '',
        'When exceeded, the API returns `429 Too Many Requests`.',
        '',
        '## Errors',
        '',
        'All errors return JSON of shape `{ "error": "<message>" }`. Validation errors',
        'additionally include `issues` (zod-formatted). HTTP status codes follow the',
        'usual semantics: `400` bad request, `401` missing/invalid key, `403` ownership',
        'mismatch, `404` not found, `415` unsupported media type, `429` rate-limited.',
        '',
        '## Webhooks',
        '',
        'You can register a webhook URL in the admin UI and PRISM will POST',
        '`job.complete` and `job.failed` events to it. The body is signed with',
        'HMAC-SHA256 over the raw request bytes — see `GET /webhooks/signature-spec`',
        'for the canonical signing details.',
      ].join('\n'),
      contact: {
        name: 'REBUS-ORBIT',
        url: 'https://github.com/REBUS-ORBIT/prism',
      },
      license: { name: 'Proprietary — Rebus Industries' },
    },
    servers: [{ url: SERVER_URL, description: 'Production' }],

    tags: [
      { name: 'Meta',    description: 'Health and metadata.' },
      { name: 'Convert', description: 'Submit a file for conversion to ORBIT.' },
      { name: 'Receive', description: 'Materialise an ORBIT version into a downloadable file (.3dm or .step).' },
      { name: 'Jobs',    description: 'Poll job status and download outputs.' },
      { name: 'Webhooks',description: 'Inspect webhook signature contract.' },
    ],

    security: [{ apiKey: [] }],

    components: {
      securitySchemes: {
        apiKey: {
          type: 'apiKey',
          in: 'header',
          name: 'X-API-Key',
          description: 'Plaintext API key minted in the PRISM admin UI. Format: `prism_<base64url>`.',
        },
      },
      headers: {
        'X-RateLimit-Limit':     { description: 'Per-minute request budget for the key.',     schema: { type: 'integer' } },
        'X-RateLimit-Remaining': { description: 'Requests remaining in the current minute.',  schema: { type: 'integer' } },
        'X-RateLimit-Reset':     { description: 'Unix epoch seconds when the bucket resets.', schema: { type: 'integer' } },
        'X-Quota-Limit':         { description: 'Monthly job-submission quota for the key.',  schema: { type: 'integer' } },
        'X-Quota-Remaining':     { description: 'Quota remaining for this calendar month.',   schema: { type: 'integer' } },
        'X-Quota-Reset':         { description: 'Unix epoch seconds when the quota resets.',  schema: { type: 'integer' } },
      },
      schemas: {
        Error: {
          type: 'object',
          required: ['error'],
          properties: {
            error:  { type: 'string', example: 'unsupported format: .xyz' },
            issues: { type: 'array', items: { type: 'object' }, description: 'Optional zod validation issues.' },
          },
        },
        JobStatus: {
          type: 'string',
          enum: ['queued', 'dispatched', 'processing', 'complete', 'failed', 'cancelled'],
        },
        JobKind: {
          type: 'string',
          enum: ['convert', 'receive'],
        },
        Job: {
          type: 'object',
          properties: {
            id:               { type: 'string', format: 'uuid' },
            status:           { $ref: '#/components/schemas/JobStatus' },
            jobType:          { $ref: '#/components/schemas/JobKind' },
            createdAt:        { type: 'string', format: 'date-time' },
            updatedAt:        { type: 'string', format: 'date-time' },
            completedAt:      { type: 'string', format: 'date-time', nullable: true },
            fileName:         { type: 'string' },
            fileSize:         { type: 'integer' },
            format:           { type: 'string', example: '.3dm' },
            orbitTarget:      { type: 'string', enum: ['prod', 'dev'] },
            projectId:        { type: 'string' },
            modelId:          { type: 'string' },
            modelName:        { type: 'string', nullable: true },
            currentStage:     { type: 'string', nullable: true,
                                example: 'meshing',
                                description: 'Current agent-side pipeline node.' },
            progressPercent:  { type: 'integer', nullable: true, minimum: 0, maximum: 100 },
            lastMessage:      { type: 'string', nullable: true },
            resultUrl:        { type: 'string', nullable: true, description: 'Deep link to the resulting ORBIT version (for convert jobs).' },
            versionId:        { type: 'string', nullable: true, description: 'ORBIT version id produced by a convert job.' },
            rootObjectId:     { type: 'string', nullable: true },
            outputs:          { type: 'object',
                                additionalProperties: { type: 'string', format: 'uri' },
                                nullable: true,
                                description: 'For convert jobs that requested extra outputs (3dm, step, glb, ifc) this maps format -> downloadable URL.' },
            receiveVersionId: { type: 'string', nullable: true, description: 'For receive jobs: the ORBIT version that was materialised.' },
            error:            { type: 'string', nullable: true },
          },
        },
        JobAccepted: {
          type: 'object',
          required: ['jobId', 'status'],
          properties: {
            jobId:  { type: 'string', format: 'uuid' },
            status: { type: 'string', example: 'queued' },
          },
        },
        WebhookSignatureSpec: {
          type: 'object',
          properties: {
            header:    { type: 'string', example: 'x-prism-signature' },
            algorithm: { type: 'string', example: 'HMAC-SHA256' },
            encoding:  { type: 'string', example: 'sha256=<hex>' },
            payload:   { type: 'string', example: 'raw request body bytes' },
          },
        },
      },
      responses: {
        Unauthorized: {
          description: 'Missing or invalid API key.',
          content: { 'application/json': { schema: { $ref: '#/components/schemas/Error' } } },
        },
        Forbidden: {
          description: 'The job belongs to a different API key.',
          content: { 'application/json': { schema: { $ref: '#/components/schemas/Error' } } },
        },
        NotFound: {
          description: 'Resource not found.',
          content: { 'application/json': { schema: { $ref: '#/components/schemas/Error' } } },
        },
        RateLimited: {
          description: 'Rate limit or monthly quota exceeded.',
          headers: {
            'X-RateLimit-Reset': { $ref: '#/components/headers/X-RateLimit-Reset' },
            'X-Quota-Reset':     { $ref: '#/components/headers/X-Quota-Reset' },
          },
          content: { 'application/json': { schema: { $ref: '#/components/schemas/Error' } } },
        },
      },
    },

    paths: {
      '/health': {
        get: {
          tags: ['Meta'],
          summary: 'Liveness probe',
          description: 'Returns 200 if the API surface is reachable with a valid key.',
          security: [{ apiKey: [] }],
          responses: {
            '200': {
              description: 'OK',
              content: {
                'application/json': {
                  schema: {
                    type: 'object',
                    properties: { status: { type: 'string', example: 'ok' }, api: { type: 'string', example: 'v1' } },
                  },
                },
              },
            },
            '401': { $ref: '#/components/responses/Unauthorized' },
            '429': { $ref: '#/components/responses/RateLimited' },
          },
        },
      },

      '/convert/async': {
        post: {
          tags: ['Convert'],
          summary: 'Submit a file for conversion -> ORBIT',
          description: [
            'Upload a CAD/mesh/IFC file. Supported formats:',
            '`.3dm .dwg .dxf .fbx .obj .stl .ply .3mf .dae .step .stp .iges .igs`.',
            '',
            'The response contains a `jobId` you can poll via `GET /jobs/{jobId}` or',
            'stream live progress over Server-Sent Events at `GET /jobs/{jobId}/stream`.',
            '',
            'On success the job\'s `status` advances to `complete` and `resultUrl`',
            'points at the resulting ORBIT version. If you set `outputFormats=glb,step`',
            'the job will additionally render those formats and publish them under',
            '`outputs.<format>` for download.',
          ].join('\n'),
          requestBody: {
            required: true,
            content: {
              'multipart/form-data': {
                schema: {
                  type: 'object',
                  required: ['file', 'projectId', 'modelId'],
                  properties: {
                    file:       { type: 'string', format: 'binary', description: 'The source file. <=1 GB.' },
                    projectId:  { type: 'string', description: 'ORBIT project id.' },
                    modelId:    { type: 'string', description: 'ORBIT model id.' },
                    modelName:  { type: 'string' },
                    orbitTarget:{ type: 'string', enum: ['prod', 'dev'], default: 'prod' },
                    swapYZ:     { type: 'boolean', default: false, description: 'Swap Y and Z axes during import (CAD ↔ DCC handedness).' },
                    quality:    { type: 'string', enum: ['sensible', 'extreme'], default: 'sensible' },
                    callbackUrl:{ type: 'string', format: 'uri', description: 'Optional webhook URL to POST a `job.complete` / `job.failed` event to (in addition to globally configured webhooks).' },
                    outputFormats: { type: 'string', description: 'Comma-separated list of additional output formats (`3dm, step, glb, ifc`). Each format is rendered and uploaded; downloadable via `GET /jobs/{id}/outputs/{format}`.' },
                    includedLayers: { type: 'string', description: 'Comma-separated layer names to include (others are skipped).' },
                    includeLayerDescendants: { type: 'boolean', default: true },
                  },
                },
              },
            },
          },
          responses: {
            '202': {
              description: 'Job accepted and queued.',
              content: { 'application/json': { schema: { $ref: '#/components/schemas/JobAccepted' } } },
            },
            '400': { description: 'Validation failed.', content: { 'application/json': { schema: { $ref: '#/components/schemas/Error' } } } },
            '401': { $ref: '#/components/responses/Unauthorized' },
            '415': { description: 'Unsupported file extension.', content: { 'application/json': { schema: { $ref: '#/components/schemas/Error' } } } },
            '429': { $ref: '#/components/responses/RateLimited' },
          },
        },
      },

      '/receive/async': {
        post: {
          tags: ['Receive'],
          summary: 'Materialise an ORBIT version into a downloadable file',
          description: [
            'Asks an agent to download an existing ORBIT version into a Rhino doc',
            'and re-export it as a single file (`3dm` or `step`). Useful when an',
            'external system needs a flat file copy of an ORBIT model snapshot.',
            '',
            'Poll `GET /jobs/{jobId}` until `status=complete`, then download via',
            '`GET /jobs/{jobId}/outputs/{format}`.',
          ].join('\n'),
          requestBody: {
            required: true,
            content: {
              'application/json': {
                schema: {
                  type: 'object',
                  required: ['projectId', 'modelId', 'versionId'],
                  properties: {
                    projectId:  { type: 'string' },
                    modelId:    { type: 'string' },
                    versionId:  { type: 'string' },
                    modelName:  { type: 'string' },
                    orbitTarget:{ type: 'string', enum: ['prod', 'dev'], default: 'prod' },
                    outputFormat:{ type: 'string', enum: ['3dm', 'step'], default: '3dm' },
                    callbackUrl:{ type: 'string', format: 'uri' },
                  },
                },
              },
            },
          },
          responses: {
            '202': {
              description: 'Job accepted and queued.',
              content: { 'application/json': { schema: { $ref: '#/components/schemas/JobAccepted' } } },
            },
            '400': { description: 'Validation failed.', content: { 'application/json': { schema: { $ref: '#/components/schemas/Error' } } } },
            '401': { $ref: '#/components/responses/Unauthorized' },
            '429': { $ref: '#/components/responses/RateLimited' },
          },
        },
      },

      '/jobs/{id}': {
        get: {
          tags: ['Jobs'],
          summary: 'Poll job status',
          description: 'Returns the full job record. The `status` field is the primary signal; treat `complete`, `failed`, and `cancelled` as terminal.',
          parameters: [{ in: 'path', name: 'id', required: true, schema: { type: 'string', format: 'uuid' } }],
          responses: {
            '200': { description: 'Current job state.', content: { 'application/json': { schema: { $ref: '#/components/schemas/Job' } } } },
            '401': { $ref: '#/components/responses/Unauthorized' },
            '403': { $ref: '#/components/responses/Forbidden' },
            '404': { $ref: '#/components/responses/NotFound' },
            '429': { $ref: '#/components/responses/RateLimited' },
          },
        },
      },

      '/jobs/{id}/stream': {
        get: {
          tags: ['Jobs'],
          summary: 'Stream live job progress via Server-Sent Events',
          description: [
            'Returns an `text/event-stream` connection. Each frame is one of:',
            '',
            '* `event: state` — initial snapshot of the job record (single frame at connect time).',
            '* `event: update` — subsequent partial updates (progress %, stage, message, status transitions).',
            '',
            'Close the connection once you see a terminal status (`complete`, `failed`, `cancelled`).',
          ].join('\n'),
          parameters: [{ in: 'path', name: 'id', required: true, schema: { type: 'string', format: 'uuid' } }],
          responses: {
            '200': {
              description: 'SSE stream opened.',
              content: { 'text/event-stream': { schema: { type: 'string', example: 'event: update\ndata: {"progressPercent":42,"currentStage":"meshing"}\n\n' } } },
            },
            '401': { $ref: '#/components/responses/Unauthorized' },
            '404': { $ref: '#/components/responses/NotFound' },
          },
        },
      },

      '/jobs/{id}/outputs/{format}': {
        get: {
          tags: ['Jobs'],
          summary: 'Download a generated output file',
          description: 'For convert jobs that requested `outputFormats`, this streams the rendered file as `application/octet-stream`.',
          parameters: [
            { in: 'path', name: 'id',     required: true, schema: { type: 'string', format: 'uuid' } },
            { in: 'path', name: 'format', required: true, schema: { type: 'string', enum: ['3dm', 'step', 'ifc', 'glb'] } },
          ],
          responses: {
            '200': { description: 'Binary file.',
                     content: { 'application/octet-stream': { schema: { type: 'string', format: 'binary' } } } },
            '400': { description: 'Unknown format.', content: { 'application/json': { schema: { $ref: '#/components/schemas/Error' } } } },
            '401': { $ref: '#/components/responses/Unauthorized' },
            '403': { $ref: '#/components/responses/Forbidden' },
            '404': { description: 'Job not found, or output for this format has not been produced.',
                     content: { 'application/json': { schema: { $ref: '#/components/schemas/Error' } } } },
          },
        },
      },

      '/webhooks/signature-spec': {
        get: {
          tags: ['Webhooks'],
          summary: 'Inspect the webhook signature contract',
          description: 'Returns the header name, algorithm, encoding, and payload definition that PRISM uses when signing webhook POST bodies. Use this to implement signature verification on your receiver.',
          responses: {
            '200': {
              description: 'Signing spec.',
              content: { 'application/json': { schema: { $ref: '#/components/schemas/WebhookSignatureSpec' } } },
            },
            '401': { $ref: '#/components/responses/Unauthorized' },
          },
        },
      },
    },
  };
}
