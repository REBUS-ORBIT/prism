CREATE TABLE IF NOT EXISTS "admin_users" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"username" varchar(64) NOT NULL,
	"password_hash" varchar(128) NOT NULL,
	"is_active" boolean DEFAULT true NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"last_login_at" timestamp with time zone,
	CONSTRAINT "admin_users_username_unique" UNIQUE("username")
);
--> statement-breakpoint
CREATE TABLE IF NOT EXISTS "agent_sessions" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"workstation_id" uuid NOT NULL,
	"connected_at" timestamp with time zone DEFAULT now() NOT NULL,
	"last_heartbeat" timestamp with time zone,
	"remote_addr" varchar(64),
	"slots_busy" integer DEFAULT 0 NOT NULL
);
--> statement-breakpoint
CREATE TABLE IF NOT EXISTS "api_keys" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"name" varchar(128) NOT NULL,
	"key_hash" varchar(64) NOT NULL,
	"rate_limit_per_min" integer,
	"monthly_quota" integer,
	"is_active" boolean DEFAULT true NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"last_used_at" timestamp with time zone,
	CONSTRAINT "api_keys_key_hash_unique" UNIQUE("key_hash")
);
--> statement-breakpoint
CREATE TABLE IF NOT EXISTS "job_logs" (
	"id" bigint PRIMARY KEY GENERATED ALWAYS AS IDENTITY (sequence name "job_logs_id_seq" INCREMENT BY 1 MINVALUE 1 MAXVALUE 9223372036854775807 START WITH 1 CACHE 1),
	"job_id" uuid NOT NULL,
	"ts" timestamp with time zone DEFAULT now() NOT NULL,
	"level" varchar(8) NOT NULL,
	"source" varchar(16) NOT NULL,
	"message" text NOT NULL
);
--> statement-breakpoint
CREATE TABLE IF NOT EXISTS "jobs" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"status" varchar(16) DEFAULT 'queued' NOT NULL,
	"format" varchar(16) NOT NULL,
	"file_name" varchar(512) NOT NULL,
	"file_size" bigint NOT NULL,
	"file_path" text NOT NULL,
	"orbit_target" varchar(8) DEFAULT 'prod' NOT NULL,
	"project_id" varchar(32) NOT NULL,
	"model_id" varchar(32) NOT NULL,
	"model_name" varchar(256),
	"submitted_by" varchar(128),
	"options" jsonb DEFAULT '{}'::jsonb NOT NULL,
	"node_name" varchar(128),
	"agent_session_id" uuid,
	"current_stage" varchar(64),
	"progress_percent" real,
	"last_message" text,
	"result_url" text,
	"root_object_id" varchar(64),
	"version_id" varchar(64),
	"error" text,
	"callback_url" text,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL,
	"completed_at" timestamp with time zone
);
--> statement-breakpoint
CREATE TABLE IF NOT EXISTS "layer_presets" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"project_id" varchar(32) NOT NULL,
	"model_name" varchar(256) NOT NULL,
	"included_layers" jsonb DEFAULT '[]'::jsonb NOT NULL,
	"known_layers" jsonb DEFAULT '[]'::jsonb NOT NULL,
	"include_descendants" boolean DEFAULT true NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE IF NOT EXISTS "settings" (
	"key" varchar(64) PRIMARY KEY NOT NULL,
	"value" text NOT NULL,
	"updated_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE IF NOT EXISTS "webhooks" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"name" varchar(128) NOT NULL,
	"url" text NOT NULL,
	"secret" varchar(64),
	"events" jsonb DEFAULT '["job.complete","job.failed"]'::jsonb NOT NULL,
	"is_active" boolean DEFAULT true NOT NULL,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL
);
--> statement-breakpoint
CREATE TABLE IF NOT EXISTS "workstations" (
	"id" uuid PRIMARY KEY DEFAULT gen_random_uuid() NOT NULL,
	"machine_id" varchar(64) NOT NULL,
	"node_name" varchar(128) NOT NULL,
	"can_convert" boolean DEFAULT true NOT NULL,
	"can_layer" boolean DEFAULT true NOT NULL,
	"can_receive" boolean DEFAULT false NOT NULL,
	"supported_formats" jsonb DEFAULT '[]'::jsonb NOT NULL,
	"slots_total" integer DEFAULT 1 NOT NULL,
	"agent_version" varchar(32),
	"rhino_version" varchar(32),
	"is_enabled" boolean DEFAULT true NOT NULL,
	"notes" text,
	"created_at" timestamp with time zone DEFAULT now() NOT NULL,
	"last_seen_at" timestamp with time zone,
	CONSTRAINT "workstations_machine_id_unique" UNIQUE("machine_id")
);
--> statement-breakpoint
DO $$ BEGIN
 ALTER TABLE "agent_sessions" ADD CONSTRAINT "agent_sessions_workstation_id_workstations_id_fk" FOREIGN KEY ("workstation_id") REFERENCES "public"."workstations"("id") ON DELETE cascade ON UPDATE no action;
EXCEPTION
 WHEN duplicate_object THEN null;
END $$;
--> statement-breakpoint
DO $$ BEGIN
 ALTER TABLE "job_logs" ADD CONSTRAINT "job_logs_job_id_jobs_id_fk" FOREIGN KEY ("job_id") REFERENCES "public"."jobs"("id") ON DELETE cascade ON UPDATE no action;
EXCEPTION
 WHEN duplicate_object THEN null;
END $$;
--> statement-breakpoint
CREATE INDEX IF NOT EXISTS "agent_sessions_workstation_idx" ON "agent_sessions" USING btree ("workstation_id");--> statement-breakpoint
CREATE INDEX IF NOT EXISTS "job_logs_job_idx" ON "job_logs" USING btree ("job_id","ts");--> statement-breakpoint
CREATE INDEX IF NOT EXISTS "jobs_status_idx" ON "jobs" USING btree ("status");--> statement-breakpoint
CREATE INDEX IF NOT EXISTS "jobs_created_at_idx" ON "jobs" USING btree ("created_at");--> statement-breakpoint
CREATE INDEX IF NOT EXISTS "jobs_project_idx" ON "jobs" USING btree ("project_id");--> statement-breakpoint
CREATE INDEX IF NOT EXISTS "layer_presets_target_idx" ON "layer_presets" USING btree ("project_id","model_name");