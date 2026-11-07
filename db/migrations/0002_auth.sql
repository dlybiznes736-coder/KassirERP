-- =====================================================================
-- Миграция 0002: вход по паролю и PIN (бэкенд, шаг 2).
-- Применяется поверх kassir_schema_v1.sql (это миграция 0001).
-- На свежем томе выполняется автоматически (docker-entrypoint-initdb.d),
-- на существующей базе:
--   docker compose exec -T postgres psql -U postgres -d kassir -v ON_ERROR_STOP=1 -f /db/migrations/0002_auth.sql
--
-- Зачем схема auth. До входа app.tenant_id не задан, и RLS не даёт прочитать
-- пользователя. Поэтому вход идёт через узкие функции SECURITY DEFINER:
-- они возвращают ровно то, что нужно для проверки пароля, и ничего больше.
-- Других путей мимо RLS у роли kassir_app нет.
-- =====================================================================
BEGIN;

-- Код фирмы, который пользователь вводит при входе («obuv-centr»).
-- В облаке логины уникальны только внутри фирмы (ux_users_login).
ALTER TABLE tenants ADD COLUMN code text;
CREATE UNIQUE INDEX ux_tenants_code ON tenants (lower(code)) WHERE code IS NOT NULL;

ALTER TABLE tenants ALTER COLUMN schema_version SET DEFAULT 2;
UPDATE tenants SET schema_version = 2 WHERE schema_version < 2;

CREATE SCHEMA auth;
REVOKE ALL ON SCHEMA auth FROM PUBLIC;
GRANT USAGE ON SCHEMA auth TO kassir_app;

-- Счётчик неудачных попыток. Отдельная таблица, а не колонки в users:
-- иначе каждая попытка входа меняла бы users.updated_at, и кассы
-- синхронизировали бы пользователя заново.
CREATE TABLE auth.login_attempts (
  tenant_id     uuid NOT NULL REFERENCES tenants(id),
  user_id       uuid NOT NULL REFERENCES users(id),
  kind          text NOT NULL CHECK (kind IN ('password', 'pin')),
  failed        int NOT NULL DEFAULT 0,
  locked_until  timestamptz,
  PRIMARY KEY (user_id, kind)
);
ALTER TABLE auth.login_attempts ENABLE ROW LEVEL SECURITY;
ALTER TABLE auth.login_attempts FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON auth.login_attempts
  USING (tenant_id = current_tenant_id()) WITH CHECK (tenant_id = current_tenant_id());
-- Прав у kassir_app на таблицу нет: только через функции ниже.

-- Пользователь для проверки пароля/PIN. Только активные, не удалённые.
CREATE FUNCTION auth.find_user(p_tenant_code text, p_login text)
RETURNS TABLE (user_id uuid, tenant_id uuid, store_id uuid, role text,
               password_hash text, pin_hash text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
  SELECT u.id, u.tenant_id, u.store_id, u.role, u.password_hash, u.pin_hash
  FROM tenants t
  JOIN users u ON u.tenant_id = t.id
  WHERE lower(t.code) = lower(p_tenant_code) AND t.deleted_at IS NULL
    AND u.login = p_login AND u.deleted_at IS NULL AND u.is_active
$$;

-- Попытка входа резервируется ДО проверки хеша: счётчик растёт атомарно,
-- поэтому параллельные запросы не дадут перебрать больше p_max вариантов.
-- На p_max-й попытке ставится блокировка, счётчик обнуляется; если эта
-- попытка оказалась верной, auth.reset_attempts снимает блокировку.
CREATE FUNCTION auth.begin_attempt(p_user uuid, p_kind text, p_max int, p_lock_seconds int)
RETURNS TABLE (allowed boolean, retry_after_seconds int)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE lu timestamptz;
BEGIN
  INSERT INTO auth.login_attempts (tenant_id, user_id, kind)
  SELECT u.tenant_id, u.id, p_kind FROM users u WHERE u.id = p_user
  ON CONFLICT (user_id, kind) DO NOTHING;

  UPDATE auth.login_attempts a
     SET failed       = CASE WHEN a.failed + 1 >= p_max THEN 0 ELSE a.failed + 1 END,
         locked_until = CASE WHEN a.failed + 1 >= p_max
                             THEN now() + make_interval(secs => p_lock_seconds)
                             ELSE a.locked_until END
   WHERE a.user_id = p_user AND a.kind = p_kind
     AND (a.locked_until IS NULL OR a.locked_until <= now());

  IF FOUND THEN
    RETURN QUERY SELECT true, 0;
  ELSE
    SELECT a.locked_until INTO lu FROM auth.login_attempts a WHERE a.user_id = p_user AND a.kind = p_kind;
    RETURN QUERY SELECT false, greatest(1, ceil(extract(epoch FROM lu - now()))::int);
  END IF;
END $$;

CREATE FUNCTION auth.reset_attempts(p_user uuid, p_kind text)
RETURNS void
LANGUAGE sql SECURITY DEFINER SET search_path = public, pg_temp AS $$
  UPDATE auth.login_attempts SET failed = 0, locked_until = NULL
  WHERE user_id = p_user AND kind = p_kind
$$;

-- Филиал, выбранный при входе пользователем без привязки к филиалу.
CREATE FUNCTION auth.store_in_tenant(p_tenant uuid, p_store uuid)
RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
  SELECT EXISTS (SELECT 1 FROM stores
                 WHERE id = p_store AND tenant_id = p_tenant AND deleted_at IS NULL AND is_active)
$$;

REVOKE ALL ON ALL FUNCTIONS IN SCHEMA auth FROM PUBLIC;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA auth TO kassir_app;

COMMIT;
