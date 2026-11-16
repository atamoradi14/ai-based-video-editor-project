create table app_settings (
  key text primary key,
  value text not null
);

create table users (
  id uuid primary key,
  email text not null unique,
  display_name text not null,
  password_hash text not null,
  created_at timestamptz not null default now()
);

create table brand_kits (
  id uuid primary key,
  owner_id uuid references users (id),
  name text not null,
  theme_key text not null,
  definition_json text not null,
  is_system boolean not null default false
);

create table projects (
  id uuid primary key,
  owner_id uuid not null references users (id),
  title text not null,
  status text not null,
  status_detail text,
  concurrency_token integer not null default 1,
  revision integer not null default 0,
  brand_kit_id uuid references brand_kits (id),
  theme_snapshot_json text not null,
  subject text not null,
  goal text not null,
  audience text not null,
  tone text not null,
  language text not null,
  platform text not null,
  target_duration integer not null,
  call_to_action text,
  warnings_json text not null default '[]',
  publish_pack_json text,
  selected_cover_id uuid,
  current_video_asset_id uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create index projects_owner_updated on projects (owner_id, updated_at desc);

create table assets (
  id uuid primary key,
  project_id uuid not null references projects (id),
  kind text not null,
  file_name text not null,
  content_type text not null,
  size_bytes bigint not null,
  bytes bytea,
  created_at timestamptz not null default now()
);

create table jobs (
  id uuid primary key,
  project_id uuid not null references projects (id),
  kind text not null,
  status text not null,
  stage text not null,
  message text,
  attempt integer not null default 1,
  cancel_requested boolean not null default false,
  updated_at timestamptz not null default now()
);

create table revisions (
  number integer not null,
  project_id uuid not null references projects (id),
  parent_number integer,
  state text not null,
  summary text not null,
  touches_video boolean not null default false,
  touches_cover boolean not null default false,
  video_asset_id uuid,
  author text not null,
  created_at timestamptz not null default now(),
  primary key (project_id, number)
);

create table covers (
  id uuid primary key,
  project_id uuid not null references projects (id),
  asset_id uuid,
  source text not null,
  headline text not null,
  prompt text,
  selected boolean not null default false,
  revision_number integer not null default 0
);

create table messages (
  id uuid primary key,
  project_id uuid not null references projects (id),
  role text not null,
  content text not null,
  selection_json text,
  change_set_json text,
  revision_number integer,
  created_at timestamptz not null default now()
);

insert into users (id, email, display_name, password_hash) values
  ('99999999-9999-4999-8999-999999999999', 'demo@lumen.studio', 'Demo editor', 'scrypt$13d75ad75e37184946b1b7170f0ef98d$d03c014bf41a76a722261727dd821d78f119268993f2e127c4a89b24c0651ca2');

insert into brand_kits (id, owner_id, name, theme_key, definition_json, is_system) values
  ('11111111-1111-4111-8111-111111111111', null, 'Minimal', 'minimal', '{"primary":"#1C1917","accent":"#C4552A","text":"#F4EFE6","font":"Noto Naskh Arabic","captionStyle":"bold","look":"cinema"}', true),
  ('22222222-2222-4222-8222-222222222222', null, 'Professional', 'professional', '{"primary":"#102033","accent":"#D6A15A","text":"#F7F4EF","font":"Noto Naskh Arabic","captionStyle":"minimal","look":"neutral"}', true),
  ('33333333-3333-4333-8333-333333333333', null, 'Modern', 'modern', '{"primary":"#111111","accent":"#3DDC97","text":"#F4F7F5","font":"Noto Naskh Arabic","captionStyle":"bold","look":"vivid"}', true),
  ('44444444-4444-4444-8444-444444444444', null, 'Cinematic', 'cinematic', '{"primary":"#140E0A","accent":"#E7A04A","text":"#F6EFE4","font":"Noto Naskh Arabic","captionStyle":"bold","look":"cinema"}', true),
  ('55555555-5555-4555-8555-555555555555', null, 'Educational', 'educational', '{"primary":"#10261F","accent":"#F2C14E","text":"#F4F7F2","font":"Noto Naskh Arabic","captionStyle":"box","look":"neutral"}', true),
  ('66666666-6666-4666-8666-666666666666', null, 'Promotional', 'promotional', '{"primary":"#2A0E16","accent":"#FF5A36","text":"#FFF6F1","font":"Noto Naskh Arabic","captionStyle":"bold","look":"vivid"}', true),
  ('77777777-7777-4777-8777-777777777777', null, 'Luxury', 'luxury', '{"primary":"#16130F","accent":"#C6A15B","text":"#F8F1E3","font":"Noto Naskh Arabic","captionStyle":"minimal","look":"noir"}', true),
  ('88888888-8888-4888-8888-888888888888', null, 'Energetic', 'energetic', '{"primary":"#140818","accent":"#FF3D8A","text":"#FFF0F6","font":"Noto Naskh Arabic","captionStyle":"bold","look":"vivid"}', true);
