-- Phase A (REBUS-ORBIT/prism): Visualiser role plumbing.
--
-- 1) `visualiser_runs` table tracks Pixel Streaming sessions hosted on
--    visualiser agents. Lifecycle: queued -> importing -> streaming
--    -> (failed | ended). The agent reverse-channels
--    `visualisationReady` / `visualisationFailed` envelopes to fill in
--    `signalling_url`, `streamer_id`, `ready_at`, `error`, `ended_at`.
-- 2) `api_keys.scopes` jsonb array of capability scopes. Empty list
--    preserves legacy "all-of-/v1/*" behaviour for existing keys; the
--    `visualiser:create_stream` scope is added on the create-key form
--    in the admin UI and enforced by the new `requireScope` middleware.
-- 3) `workstations.can_visualise` boolean — admin role toggle that
--    Phase G's `tryDispatchVisualisation` filters on (alongside slot
--    availability). Defaults to false so existing rows are not surprised.
--
-- Note: the previous migration (`0002_layer_selection.sql`) was
-- hand-written without a corresponding `meta/0002_snapshot.json`. We
-- intentionally skip the snapshot for 0002 here; drizzle's next
-- `db:generate` run after 0003 will diff against `meta/0003_snapshot.json`
-- (which carries the full current schema state), so no future spurious
-- diffs against the 0001 snapshot will recur.
CREATE TABLE IF NOT EXISTS "visualiser_runs" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"status" varchar(16) DEFAULT 'queued' NOT NULL,
	"orbit_target" varchar(8) DEFAULT 'prod' NOT NULL,
	"project_id" varchar(32) NOT NULL,
	"model_id" varchar(32) NOT NULL,
	"version_id" varchar(64),
	"template_tag" varchar(64),
	"workstation_id" uuid,
	"agent_session_id" uuid,
	"signalling_url" text,
	"streamer_id" varchar(64),
	"submitted_by" varchar(128),
	"ttl_seconds" integer,
	"callback_url" text,
	"error" text,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL,
	"dispatched_at" timestamp with time zone,
	"ready_at" timestamp with time zone,
	"ended_at" timestamp with time zone
);
--> statement-breakpoint
ALTER TABLE "api_keys" ADD COLUMN IF NOT EXISTS "scopes" jsonb DEFAULT '[]'::jsonb NOT NULL;--> statement-breakpoint
ALTER TABLE "workstations" ADD COLUMN IF NOT EXISTS "can_visualise" boolean DEFAULT false NOT NULL;--> statement-breakpoint
DO $$ BEGIN
 ALTER TABLE "visualiser_runs" ADD CONSTRAINT "visualiser_runs_workstation_id_workstations_id_fk" FOREIGN KEY ("workstation_id") REFERENCES "public"."workstations"("id") ON DELETE set null ON UPDATE no action;
EXCEPTION
 WHEN duplicate_object THEN null;
END $$;
--> statement-breakpoint
CREATE INDEX IF NOT EXISTS "visualiser_runs_status_idx" ON "visualiser_runs" USING btree ("status");--> statement-breakpoint
CREATE INDEX IF NOT EXISTS "visualiser_runs_created_at_idx" ON "visualiser_runs" USING btree ("created_at");--> statement-breakpoint
CREATE INDEX IF NOT EXISTS "visualiser_runs_project_idx" ON "visualiser_runs" USING btree ("project_id");
