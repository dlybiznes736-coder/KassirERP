-- =====================================================================
-- Миграция 0004: регистрация касс (DESIGN_GROCERY.md, п. 4).
--
-- 1. Таблицы devices и device_enrollments (код подключения от менеджера).
-- 2. device_id в cash_shifts, sales, sync_outbox: был произвольный текст,
--    становится uuid со ссылкой на devices. ЛОМАЕТ закрытую схему — делаем
--    сейчас, пока нет живых установок (решение 2026-11-07).
--    Данные переносятся: на каждое старое имя кассы (тенант, филиал, имя)
--    создаётся строка devices без секрета — войти с неё нельзя, кассу
--    нужно зарегистрировать заново. История чеков и смен сохраняется.
-- 3. Функции входа в схеме auth: проверка кассы, вход по PIN в тенанте
--    кассы, регистрация по коду, список отключённых касс.
-- =====================================================================
BEGIN;

-- ---------------------------------------------------------------------
-- 1. Кассы
-- ---------------------------------------------------------------------
CREATE TABLE devices (
  id             uuid PRIMARY KEY,
  tenant_id      uuid NOT NULL REFERENCES tenants(id),
  store_id       uuid NOT NULL REFERENCES stores(id),
  name           text NOT NULL,
  -- SHA-256 (hex) случайного секрета 32 байта. Секрет длинный и случайный —
  -- соль и PBKDF2 не нужны. 'none' — касса без секрета (перенесена из старых
  -- данных или создана генератором): войти с неё нельзя.
  secret_hash    text NOT NULL,
  registered_by  uuid REFERENCES users(id),
  registered_at  timestamptz NOT NULL DEFAULT now(),
  last_seen_at   timestamptz,
  revoked_at     timestamptz
);
CREATE INDEX ix_devices_store ON devices (tenant_id, store_id);

-- Одноразовый код подключения кассы: 8 цифр, 10 минут, хранится только хеш.
CREATE TABLE device_enrollments (
  id          uuid PRIMARY KEY,
  tenant_id   uuid NOT NULL REFERENCES tenants(id),
  store_id    uuid NOT NULL REFERENCES stores(id),
  code_hash   text NOT NULL,
  expires_at  timestamptz NOT NULL,
  created_by  uuid NOT NULL REFERENCES users(id),
  created_at  timestamptz NOT NULL DEFAULT now(),
  used_at     timestamptz,
  device_id   uuid REFERENCES devices(id)
);
-- Среди неиспользованных кодов хеш уникален: код находится без тенанта.
CREATE UNIQUE INDEX ux_enrollments_active_code ON device_enrollments (code_hash) WHERE used_at IS NULL;

ALTER TABLE devices ENABLE ROW LEVEL SECURITY;
ALTER TABLE devices FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON devices
  USING (tenant_id = current_tenant_id()) WITH CHECK (tenant_id = current_tenant_id());
ALTER TABLE device_enrollments ENABLE ROW LEVEL SECURITY;
ALTER TABLE device_enrollments FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON device_enrollments
  USING (tenant_id = current_tenant_id()) WITH CHECK (tenant_id = current_tenant_id());

-- Приложение: список касс и отключение — UPDATE; секрет и тенант не меняются.
GRANT SELECT, INSERT ON devices, device_enrollments TO kassir_app;
GRANT UPDATE (revoked_at, name) ON devices TO kassir_app;

-- ---------------------------------------------------------------------
-- 2. device_id: текст → uuid со ссылкой на devices
-- ---------------------------------------------------------------------
CREATE TEMP TABLE old_devices ON COMMIT DROP AS
SELECT tenant_id, store_id, device_id AS name, min(at) AS first_seen, uuid_v7() AS id
FROM (SELECT tenant_id, store_id, device_id, opened_at AS at FROM cash_shifts
      UNION ALL
      SELECT tenant_id, store_id, device_id, created_at_local FROM sales) x
GROUP BY tenant_id, store_id, device_id;

INSERT INTO devices (id, tenant_id, store_id, name, secret_hash, registered_at)
SELECT id, tenant_id, store_id, name, 'none', first_seen FROM old_devices;

-- cash_shifts
ALTER TABLE cash_shifts ADD COLUMN device_ref uuid;
UPDATE cash_shifts c SET device_ref = d.id
FROM old_devices d WHERE d.tenant_id = c.tenant_id AND d.store_id = c.store_id AND d.name = c.device_id;
DROP INDEX ux_shifts_open_per_device;
ALTER TABLE cash_shifts DROP COLUMN device_id;
ALTER TABLE cash_shifts RENAME COLUMN device_ref TO device_id;
ALTER TABLE cash_shifts ALTER COLUMN device_id SET NOT NULL;
ALTER TABLE cash_shifts ADD FOREIGN KEY (device_id) REFERENCES devices(id);
CREATE UNIQUE INDEX ux_shifts_open_per_device ON cash_shifts (tenant_id, device_id) WHERE status = 'open';

-- sales (UPDATE разрешён: запрещено только DELETE чека)
ALTER TABLE sales ADD COLUMN device_ref uuid;
UPDATE sales s SET device_ref = d.id
FROM old_devices d WHERE d.tenant_id = s.tenant_id AND d.store_id = s.store_id AND d.name = s.device_id;
DROP INDEX ux_sales_device_number;
ALTER TABLE sales DROP COLUMN device_id;
ALTER TABLE sales RENAME COLUMN device_ref TO device_id;
ALTER TABLE sales ALTER COLUMN device_id SET NOT NULL;
ALTER TABLE sales ADD FOREIGN KEY (device_id) REFERENCES devices(id);
CREATE UNIQUE INDEX ux_sales_device_number ON sales (tenant_id, device_id, local_number);

-- sync_outbox: филиала там нет — сопоставляем по (тенант, имя), если имя однозначно.
ALTER TABLE sync_outbox ADD COLUMN device_ref uuid;
UPDATE sync_outbox o SET device_ref = d.id
FROM (SELECT tenant_id, name, min(id::text)::uuid AS id FROM old_devices GROUP BY tenant_id, name HAVING count(*) = 1) d
WHERE d.tenant_id = o.tenant_id AND d.name = o.device_id;
ALTER TABLE sync_outbox DROP COLUMN device_id;
ALTER TABLE sync_outbox RENAME COLUMN device_ref TO device_id;
ALTER TABLE sync_outbox ADD FOREIGN KEY (device_id) REFERENCES devices(id);

-- ---------------------------------------------------------------------
-- 3. Вход: функции схемы auth (до токена тенант неизвестен)
-- ---------------------------------------------------------------------

-- Касса для входа по PIN. Отмечает, что касса выходила на связь.
CREATE FUNCTION auth.find_device(p_device uuid)
RETURNS TABLE (device_id uuid, tenant_id uuid, store_id uuid, secret_hash text, revoked boolean)
LANGUAGE sql SECURITY DEFINER SET search_path = public, pg_temp AS $$
  UPDATE devices d SET last_seen_at = now()
  WHERE d.id = p_device
  RETURNING d.id, d.tenant_id, d.store_id, d.secret_hash, d.revoked_at IS NOT NULL
$$;

-- Пользователь по логину в тенанте кассы (вход по PIN без кода фирмы).
CREATE FUNCTION auth.find_user_in_tenant(p_tenant uuid, p_login text)
RETURNS TABLE (user_id uuid, tenant_id uuid, store_id uuid, role text, password_hash text, pin_hash text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
  SELECT u.id, u.tenant_id, u.store_id, u.role, u.password_hash, u.pin_hash
  FROM users u JOIN tenants t ON t.id = u.tenant_id
  WHERE u.tenant_id = p_tenant AND t.deleted_at IS NULL
    AND u.login = p_login AND u.deleted_at IS NULL AND u.is_active
$$;

-- Регистрация кассы по коду: код одноразовый, проверка и погашение атомарны.
-- Нет действующего кода — пустой результат.
CREATE FUNCTION auth.enroll_device(p_code_hash text, p_device uuid, p_name text, p_secret_hash text)
RETURNS TABLE (tenant_id uuid, store_id uuid)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE e device_enrollments;
BEGIN
  SELECT * INTO e FROM device_enrollments
   WHERE code_hash = p_code_hash AND used_at IS NULL AND expires_at > now()
   FOR UPDATE;
  IF NOT FOUND THEN
    RETURN;
  END IF;

  INSERT INTO devices (id, tenant_id, store_id, name, secret_hash, registered_by)
  VALUES (p_device, e.tenant_id, e.store_id, p_name, p_secret_hash, e.created_by);
  UPDATE device_enrollments SET used_at = now(), device_id = p_device WHERE id = e.id;

  RETURN QUERY SELECT e.tenant_id, e.store_id;
END $$;

-- Отключённые кассы для кэша API: их токены отклоняются сразу, а не по
-- истечении (12 ч). Старше 13 ч не нужны — таких токенов уже нет.
CREATE FUNCTION auth.revoked_devices()
RETURNS SETOF uuid
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
  SELECT id FROM devices WHERE revoked_at > now() - interval '13 hours'
$$;

REVOKE ALL ON ALL FUNCTIONS IN SCHEMA auth FROM PUBLIC;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA auth TO kassir_app;

ALTER TABLE tenants ALTER COLUMN schema_version SET DEFAULT 4;
UPDATE tenants SET schema_version = 4 WHERE schema_version < 4;

COMMIT;
