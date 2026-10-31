-- EvoFlow: single-device public client, user/workspace authorization and private artifacts.
create table public.workspaces (
  id uuid primary key default gen_random_uuid(),
  owner_id uuid not null references auth.users(id) on delete cascade,
  name text not null check (length(name) between 1 and 120),
  created_at timestamptz not null default now()
);
create index workspaces_owner_idx on public.workspaces(owner_id);
create table public.memberships (
  workspace_id uuid not null references public.workspaces(id) on delete cascade,
  user_id uuid not null references auth.users(id) on delete cascade,
  created_at timestamptz not null default now(),
  primary key(workspace_id,user_id)
);
create index memberships_user_idx on public.memberships(user_id);
create table public.records (
  id uuid not null,
  workspace_id uuid not null references public.workspaces(id) on delete cascade,
  kind text not null check (kind in ('run','run_event','skill','skill_version','skill_evaluation',
                                   'artifact','benchmark','knowledge','knowledge_chunk','preference')),
  owner_id uuid not null references auth.users(id) on delete cascade,
  payload jsonb not null check (octet_length(payload::text) <= 2000000),
  updated_at timestamptz not null default now(),
  primary key(id,workspace_id,kind)
);
create index records_owner_idx on public.records(owner_id);
create index records_workspace_kind_idx on public.records(workspace_id,kind);
alter table public.workspaces enable row level security;
alter table public.memberships enable row level security;
alter table public.records enable row level security;
create policy workspace_read on public.workspaces for select to authenticated using (
 owner_id=(select auth.uid()) or id in (select workspace_id from public.memberships where user_id=(select auth.uid()))
);
create policy workspace_insert on public.workspaces for insert to authenticated with check(owner_id=(select auth.uid()));
create policy workspace_update on public.workspaces for update to authenticated
 using(owner_id=(select auth.uid())) with check(owner_id=(select auth.uid()));
create policy workspace_delete on public.workspaces for delete to authenticated using(owner_id=(select auth.uid()));
create policy membership_read on public.memberships for select to authenticated using(user_id=(select auth.uid()));
create policy membership_insert on public.memberships for insert to authenticated with check(
 workspace_id in (select id from public.workspaces where owner_id=(select auth.uid()))
);
create policy membership_delete on public.memberships for delete to authenticated using(
 workspace_id in (select id from public.workspaces where owner_id=(select auth.uid())) or user_id=(select auth.uid())
);
create policy records_read on public.records for select to authenticated using (
 workspace_id in (select id from public.workspaces) and (kind <> 'preference' or owner_id=(select auth.uid()))
);
create policy records_insert on public.records for insert to authenticated with check (
 owner_id=(select auth.uid()) and workspace_id in (select id from public.workspaces)
);
create policy records_update on public.records for update to authenticated using (
 owner_id=(select auth.uid()) and workspace_id in (select id from public.workspaces)
) with check(owner_id=(select auth.uid()) and workspace_id in (select id from public.workspaces));
create policy records_delete on public.records for delete to authenticated using (
 owner_id=(select auth.uid()) and workspace_id in (select id from public.workspaces)
);
revoke all on public.workspaces,public.memberships,public.records from anon;
grant select,insert,update,delete on public.workspaces,public.records to authenticated;
grant select,insert,delete on public.memberships to authenticated;
-- Compact metadata uses one RLS-protected physical store, with typed invoker views.
create view public.runs with (security_invoker=true) as select * from public.records where kind='run';
create view public.run_events with (security_invoker=true) as select * from public.records where kind='run_event';
create view public.skills with (security_invoker=true) as select * from public.records where kind='skill';
create view public.skill_versions with (security_invoker=true) as select * from public.records where kind='skill_version';
create view public.skill_evaluations with (security_invoker=true) as select * from public.records where kind='skill_evaluation';
create view public.artifacts with (security_invoker=true) as select * from public.records where kind='artifact';
create view public.benchmark_results with (security_invoker=true) as select * from public.records where kind='benchmark';
create view public.knowledge_entries with (security_invoker=true) as select * from public.records where kind='knowledge';
create view public.knowledge_chunks with (security_invoker=true) as select * from public.records where kind='knowledge_chunk';
create view public.preferences with (security_invoker=true) as select * from public.records where kind='preference';
grant select on public.runs,public.run_events,public.skills,public.skill_versions,public.skill_evaluations,
 public.artifacts,public.benchmark_results,public.knowledge_entries,public.knowledge_chunks,public.preferences to authenticated;
insert into storage.buckets(id,name,public,file_size_limit,allowed_mime_types)
 values('evoflow-private','evoflow-private',false,2000000,array['application/json','text/plain','text/markdown']);
create policy evoflow_storage_read on storage.objects for select to authenticated using (
 bucket_id='evoflow-private' and (storage.foldername(name))[1] in (select id::text from public.workspaces)
);
create policy evoflow_storage_insert on storage.objects for insert to authenticated with check (
 bucket_id='evoflow-private' and (storage.foldername(name))[1] in (select id::text from public.workspaces)
);
create policy evoflow_storage_update on storage.objects for update to authenticated using (
 bucket_id='evoflow-private' and owner_id=(select auth.uid())::text and
 (storage.foldername(name))[1] in (select id::text from public.workspaces)
) with check(bucket_id='evoflow-private' and owner_id=(select auth.uid())::text and
 (storage.foldername(name))[1] in (select id::text from public.workspaces));
create policy evoflow_storage_delete on storage.objects for delete to authenticated using (
 bucket_id='evoflow-private' and owner_id=(select auth.uid())::text and
 (storage.foldername(name))[1] in (select id::text from public.workspaces)
);
