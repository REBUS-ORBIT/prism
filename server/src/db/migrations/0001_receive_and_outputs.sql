ALTER TABLE "jobs" ADD COLUMN "job_type" varchar(16) DEFAULT 'convert' NOT NULL;--> statement-breakpoint
ALTER TABLE "jobs" ADD COLUMN "receive_version_id" varchar(64);--> statement-breakpoint
ALTER TABLE "jobs" ADD COLUMN "output_formats" jsonb DEFAULT '[]'::jsonb NOT NULL;--> statement-breakpoint
ALTER TABLE "jobs" ADD COLUMN "outputs" jsonb DEFAULT '{}'::jsonb NOT NULL;--> statement-breakpoint
CREATE INDEX IF NOT EXISTS "jobs_job_type_idx" ON "jobs" USING btree ("job_type");