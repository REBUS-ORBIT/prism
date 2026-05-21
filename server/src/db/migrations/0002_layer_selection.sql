-- Phase 6 (REBUS-ORBIT/prism): two-phase pollLayers / convert flow.
--
-- jobs.status is widened to varchar(24) so it can hold the new
-- `awaiting_selection` state (the longest value we now use, 18 chars).
-- The existing values (queued / dispatched / processing / complete /
-- failed / cancelled / uploading) all fit in 16 chars, so old rows are
-- forward-compatible.
ALTER TABLE "jobs" ALTER COLUMN "status" TYPE varchar(24);--> statement-breakpoint
ALTER TABLE "jobs" ADD COLUMN "select_layers" boolean DEFAULT false NOT NULL;--> statement-breakpoint
ALTER TABLE "jobs" ADD COLUMN "layers_json" jsonb;--> statement-breakpoint
ALTER TABLE "jobs" ADD COLUMN "included_layers" jsonb;--> statement-breakpoint
ALTER TABLE "jobs" ADD COLUMN "include_layer_descendants" boolean DEFAULT false NOT NULL;
