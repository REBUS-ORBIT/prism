-- Phase G (REBUS-ORBIT/prism): server API + signalling proxy + dispatcher.
--
-- Adds the columns the new `/api/visualiser/*` REST surface and
-- `tryDispatchVisualisation` need that Phase A's scaffold didn't cover:
--
--   1) `workstations.current_visualiser_load int` — atomic per-workstation
--      counter the dispatcher locks at pick time and decrements when the
--      agent reports `visualisationEnded` / `visualisationFailed`.
--      Separate from `agent_sessions.slots_busy` because a Visualiser
--      run is a long-lived UE process that saturates the host's GPU,
--      whereas conversion slots are short-lived Rhino jobs.
--
--   2) `visualiser_runs.player_url text` — public deep-link to the
--      embedded debug Pixel Streaming player. Phase I replaces the
--      iframe shim with a real Pixel Streaming frontend; Phase G just
--      persists `${PUBLIC_BASE_URL}/admin/#/visualiser/<runId>` so the
--      portal response carries the URL the operator can paste.
--
--   3) `visualiser_runs.failure_reason varchar(64)` — machine-readable
--      failure code distinct from the free-form `error` message. The
--      portal contract documents the catalog of codes (`start_timeout`,
--      `no_workstation_available`, `agent_failed`, …) so callers can
--      switch on it without text-matching.
--
--   4) `visualiser_runs.requested_by_api_key_id uuid` — FK to `api_keys`
--      with ON DELETE SET NULL. Used by `DELETE /api/visualiser/streams/:runId`
--      to enforce that an API key may only stop streams it started.
--      Admin sessions bypass this check.
ALTER TABLE "visualiser_runs" ADD COLUMN "player_url" text;--> statement-breakpoint
ALTER TABLE "visualiser_runs" ADD COLUMN "requested_by_api_key_id" uuid;--> statement-breakpoint
ALTER TABLE "visualiser_runs" ADD COLUMN "failure_reason" varchar(64);--> statement-breakpoint
ALTER TABLE "workstations" ADD COLUMN "current_visualiser_load" integer DEFAULT 0 NOT NULL;--> statement-breakpoint
DO $$ BEGIN
 ALTER TABLE "visualiser_runs" ADD CONSTRAINT "visualiser_runs_requested_by_api_key_id_api_keys_id_fk" FOREIGN KEY ("requested_by_api_key_id") REFERENCES "public"."api_keys"("id") ON DELETE set null ON UPDATE no action;
EXCEPTION
 WHEN duplicate_object THEN null;
END $$;
