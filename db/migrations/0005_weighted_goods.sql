-- =====================================================================
-- Миграция 0005: весовой товар (DESIGN_GROCERY.md, п. 1).
--
-- 1. Размер и цвет пары — необязательны: у продукта их нет. Уникальность
--    (товар, размер, цвет) остаётся, NULLS NOT DISTINCT — у товара может
--    быть одна пара без размера и цвета. Ограничение только смягчается:
--    обувные данные и старый код продолжают работать.
-- 2. product_variants.unit (pcs | kg) и plu — код товара на весах (5 цифр,
--    уникален в фирме, только у весовых).
-- 3. weight_barcode_prefixes — какие префиксы 20–29 фирма отдала весам.
--    Нет ни одной строки — весовыми считаются все 20–29.
-- 4. sale_items.unit и scanned_code — копия единицы и отсканированный код
--    (принцип 5: копируем в документ).
-- =====================================================================
BEGIN;

ALTER TABLE product_variants ALTER COLUMN size DROP NOT NULL;
ALTER TABLE product_variants ALTER COLUMN color DROP NOT NULL;
DROP INDEX ux_variants_product_size_color;
CREATE UNIQUE INDEX ux_variants_product_size_color ON product_variants (product_id, size, color)
  NULLS NOT DISTINCT WHERE deleted_at IS NULL;

ALTER TABLE product_variants
  ADD COLUMN unit text NOT NULL DEFAULT 'pcs' CHECK (unit IN ('pcs', 'kg')),
  ADD COLUMN plu  text CHECK (plu ~ '^[0-9]{5}$'),
  ADD CONSTRAINT ck_variants_plu_only_weighted CHECK (plu IS NULL OR unit = 'kg');

-- Скан весового: пара ищется по (tenant_id, plu) — равенство, работает под RLS.
CREATE UNIQUE INDEX ux_variants_plu ON product_variants (tenant_id, plu)
  WHERE plu IS NOT NULL AND deleted_at IS NULL;

CREATE TABLE weight_barcode_prefixes (
  tenant_id  uuid NOT NULL REFERENCES tenants(id),
  prefix     text NOT NULL CHECK (prefix ~ '^2[0-9]$'),
  PRIMARY KEY (tenant_id, prefix)
);
ALTER TABLE weight_barcode_prefixes ENABLE ROW LEVEL SECURITY;
ALTER TABLE weight_barcode_prefixes FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON weight_barcode_prefixes
  USING (tenant_id = current_tenant_id()) WITH CHECK (tenant_id = current_tenant_id());
GRANT SELECT, INSERT, DELETE ON weight_barcode_prefixes TO kassir_app;

ALTER TABLE sale_items
  ADD COLUMN unit text NOT NULL DEFAULT 'pcs' CHECK (unit IN ('pcs', 'kg')),
  ADD COLUMN scanned_code text;

ALTER TABLE tenants ALTER COLUMN schema_version SET DEFAULT 5;
UPDATE tenants SET schema_version = 5 WHERE schema_version < 5;

COMMIT;
