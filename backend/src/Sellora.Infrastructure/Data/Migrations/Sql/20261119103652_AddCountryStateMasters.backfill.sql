-- country-state-masters backfill (design section 7). FROZEN: part of migration AddCountryStateMasters; a test pins its SHA-256.
-- Must run in ONE transaction (ON COMMIT DROP temp tables): the migration never sets suppressTransaction. To re-run by hand wrap in BEGIN/COMMIT (or psql -1).
-- The text must not contain the dollar tag the EF idempotent script uses to wrap SQL; the function below uses the f-tag.
CREATE OR REPLACE FUNCTION pg_temp.geo_norm(t text) RETURNS text LANGUAGE sql IMMUTABLE AS $f$
  SELECT nullif(btrim(regexp_replace(regexp_replace(regexp_replace(
    replace(lower(coalesce(t,'')),'&',' and '),
    '[-_/,]',' ','g'), '[.\u0027\u2019\u2018`]','','g'),
    '[\t\n\v\f\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]+',' ','g')),'') $f$;

CREATE TEMP TABLE _geo_alias(alias text, cc text, scode text, india_only boolean) ON COMMIT DROP;
INSERT INTO _geo_alias(alias,cc,scode,india_only) VALUES
  ('andaman and nicobar','IN','AN',false),
  ('a&n islands','IN','AN',false),
  ('andaman nicobar','IN','AN',false),
  ('andaman','IN','AN',false),
  ('andhra','IN','AP',false),
  ('andhrapradesh','IN','AP',false),
  ('andra pradesh','IN','AP',false),
  ('arunachal','IN','AR',false),
  ('behar','IN','BR',false),
  ('ct','IN','CG',true),
  ('chattisgarh','IN','CG',false),
  ('chhatisgarh','IN','CG',false),
  ('chattisgadh','IN','CG',false),
  ('chandigarh ut','IN','CH',false),
  ('dnhdd','IN','DH',true),
  ('dnh','IN','DH',true),
  ('dn','IN','DH',true),
  ('dd','IN','DH',true),
  ('dadra and nagar haveli','IN','DH',false),
  ('daman and diu','IN','DH',false),
  ('daman','IN','DH',false),
  ('diu','IN','DH',false),
  ('new delhi','IN','DL',false),
  ('nct of delhi','IN','DL',false),
  ('nct delhi','IN','DL',false),
  ('national capital territory of delhi','IN','DL',false),
  ('gujrat','IN','GJ',false),
  ('himachal','IN','HP',false),
  ('hariyana','IN','HR',false),
  ('jharkand','IN','JH',false),
  ('j&k','IN','JK',false),
  ('jammu kashmir','IN','JK',false),
  ('jammu','IN','JK',false),
  ('kashmir','IN','JK',false),
  ('karnatak','IN','KA',false),
  ('keralam','IN','KL',false),
  ('ladak','IN','LA',false),
  ('leh','IN','LA',false),
  ('leh ladakh','IN','LA',false),
  ('lakshdweep','IN','LD',false),
  ('maharastra','IN','MH',false),
  ('orissa','IN','OD',false),
  ('odissa','IN','OD',false),
  ('panjab','IN','PB',false),
  ('pondicherry','IN','PY',false),
  ('pondichery','IN','PY',false),
  ('puduchery','IN','PY',false),
  ('pondy','IN','PY',false),
  ('rajastan','IN','RJ',false),
  ('tamilnadu','IN','TN',false),
  ('tg','IN','TS',true),
  ('telengana','IN','TS',false),
  ('uttaranchal','IN','UK',false),
  ('uttranchal','IN','UK',false),
  ('uttrakhand','IN','UK',false),
  ('uttarkhand','IN','UK',false),
  ('bengal','IN','WB',false),
  ('paschim banga','IN','WB',false),
  ('abu zaby','AE','AZ',false),
  ('abu zabi','AE','AZ',false),
  ('abudhabi','AE','AZ',false),
  ('auh','AE','AZ',false),
  ('al ain','AE','AZ',false),
  ('dubayy','AE','DU',false),
  ('dxb','AE','DU',false),
  ('ash shariqah','AE','SH',false),
  ('al sharjah','AE','SH',false),
  ('shj','AE','SH',false),
  ('umm al qaywayn','AE','UQ',false),
  ('umm al qaiwain','AE','UQ',false),
  ('uaq','AE','UQ',false),
  ('ra''s al khaymah','AE','RK',false),
  ('ras al khaima','AE','RK',false),
  ('rak','AE','RK',false),
  ('al fujayrah','AE','FU',false),
  ('al fujairah','AE','FU',false),
  ('fujeirah','AE','FU',false),
  ('ar riyad','SA','01',false),
  ('al riyadh','SA','01',false),
  ('mecca','SA','02',false),
  ('mekkah','SA','02',false),
  ('makkah al mukarramah','SA','02',false),
  ('jeddah','SA','02',false),
  ('jiddah','SA','02',false),
  ('medina','SA','03',false),
  ('madina','SA','03',false),
  ('al madinah','SA','03',false),
  ('al madinah al munawwarah','SA','03',false),
  ('eastern region','SA','04',false),
  ('dammam','SA','04',false),
  ('khobar','SA','04',false),
  ('qassim','SA','05',false),
  ('al qasim','SA','05',false),
  ('hayil','SA','06',false),
  ('northern border','SA','08',false),
  ('al hudud ash shamaliyah','SA','08',false),
  ('jizan','SA','09',false),
  ('gizan','SA','09',false),
  ('nejran','SA','10',false),
  ('al baha','SA','11',false),
  ('baha','SA','11',false),
  ('al jouf','SA','12',false),
  ('jouf','SA','12',false),
  ('aseer','SA','14',false),
  ('abha','SA','14',false),
  ('ad dawhah','QA','DA',false),
  ('al doha','QA','DA',false),
  ('al khor','QA','KH',false),
  ('al khawr','QA','KH',false),
  ('al khawr wa adh dhakhirah','QA','KH',false),
  ('ash shamal','QA','MS',false),
  ('shamal','QA','MS',false),
  ('madinat ash shamal','QA','MS',false),
  ('ar rayyan','QA','RA',false),
  ('rayyan','QA','RA',false),
  ('ash shihaniyah','QA','SH',false),
  ('al sheehaniya','QA','SH',false),
  ('shahaniya','QA','SH',false),
  ('umm slal','QA','US',false),
  ('al wakra','QA','WA',false),
  ('wakrah','QA','WA',false),
  ('wakra','QA','WA',false),
  ('az zaayin','QA','ZA',false),
  ('daayen','QA','ZA',false),
  ('kuwait city','KW','KU',false),
  ('hawally','KW','HA',false),
  ('farwaniya','KW','FA',false),
  ('al farwaniya','KW','FA',false),
  ('ahmadi','KW','AH',false),
  ('jahra','KW','JA',false),
  ('mubarak al kabir','KW','MU',false),
  ('manama','BH','13',false),
  ('al janubiyah','BH','14',false),
  ('southern governorate','BH','14',false),
  ('al muharraq','BH','15',false),
  ('ash shamaliyah','BH','17',false),
  ('al shamaliyah','BH','17',false),
  ('northern governorate','BH','17',false),
  ('masqat','OM','MA',false),
  ('zufar','OM','ZU',false),
  ('salalah','OM','ZU',false),
  ('al buraymi','OM','BU',false),
  ('buraimi','OM','BU',false),
  ('al dakhiliyah','OM','DA',false),
  ('dakhiliya','OM','DA',false),
  ('dakhiliyah','OM','DA',false),
  ('shamal al batinah','OM','BS',false),
  ('al batinah north','OM','BS',false),
  ('batinah north','OM','BS',false),
  ('janub al batinah','OM','BJ',false),
  ('al batinah south','OM','BJ',false),
  ('batinah south','OM','BJ',false),
  ('shamal ash sharqiyah','OM','SS',false),
  ('ash sharqiyah north','OM','SS',false),
  ('janub ash sharqiyah','OM','SJ',false),
  ('ash sharqiyah south','OM','SJ',false),
  ('wusta','OM','WU',false),
  ('az zahirah','OM','ZA',false),
  ('al dhahirah','OM','ZA',false),
  ('dhahira','OM','ZA',false),
  ('dhahirah','OM','ZA',false);
UPDATE _geo_alias SET alias = pg_temp.geo_norm(alias);

CREATE TEMP TABLE _geo_deny(norm text) ON COMMIT DROP;
INSERT INTO _geo_deny VALUES ('capital'),('capital governorate'),('al asimah'),('asimah'),('northern'),('southern'),
  ('eastern'),('western'),('central'),('sharqiyah'),('ash sharqiyah'),('batinah'),('al batinah'),('ncr'),('delhi ncr');

CREATE TEMP TABLE _geo_suffix(suffix text, cc text) ON COMMIT DROP;
INSERT INTO _geo_suffix VALUES ('state','IN'),('emirate','AE'),('region','SA'),('province','SA'),
  ('governorate','KW'),('governorate','BH'),('governorate','OM'),('municipality','QA');

CREATE TEMP TABLE _geo_cand(norm text, cc text, "StateId" uuid, india_only boolean) ON COMMIT DROP;
INSERT INTO _geo_cand
  SELECT pg_temp.geo_norm(s."Name"), c."Code", s."Id", false FROM "States" s JOIN "Countries" c ON c."Id"=s."CountryId"
  UNION ALL SELECT a.alias, a.cc, s."Id", a.india_only FROM _geo_alias a
            JOIN "Countries" c ON c."Code"=a.cc JOIN "States" s ON s."CountryId"=c."Id" AND s."Code"=a.scode
  UNION ALL SELECT pg_temp.geo_norm('IN-'||s."Code"), 'IN', s."Id", true FROM "States" s JOIN "Countries" c ON c."Id"=s."CountryId" WHERE c."Code"='IN'
  UNION ALL SELECT pg_temp.geo_norm(s."Code"), 'IN', s."Id", true FROM "States" s JOIN "Countries" c ON c."Id"=s."CountryId" WHERE c."Code"='IN'
  UNION ALL SELECT s."GstStateCode", 'IN', s."Id", true FROM "States" s WHERE s."GstStateCode" IS NOT NULL
  UNION ALL SELECT o.code, 'IN', s."Id", true FROM (VALUES ('in or','OD'),('in ct','CG'),('in tg','TS'),('in ut','UK'),('in ul','UK'),
            ('in dd','DH'),('in dn','DH'),('25','DH')) o(code,sc)
            JOIN "States" s ON s."Code"=o.sc AND s."CountryId"=(SELECT "Id" FROM "Countries" WHERE "Code"='IN');
DELETE FROM _geo_cand WHERE norm IN (SELECT norm FROM _geo_deny);

CREATE TEMP TABLE _geo_gst(prefix text, "StateId" uuid) ON COMMIT DROP;   -- GSTIN prefix -> state
INSERT INTO _geo_gst
  SELECT s."GstStateCode", s."Id" FROM "States" s WHERE s."GstStateCode" IS NOT NULL
  UNION ALL SELECT '25', s."Id" FROM "States" s JOIN "Countries" c ON c."Id"=s."CountryId" WHERE c."Code"='IN' AND s."Code"='DH';

-- ===== Branches: pass 1 (text) =====
WITH src AS (
  SELECT b."Id", pg_temp.geo_norm(b."State") AS n0, x.gstin
  FROM "Branches" b
  CROSS JOIN LATERAL (SELECT upper(btrim(coalesce(nullif(btrim(b."Gstin"),''),
          (SELECT bs."Gstin" FROM "BusinessSettings" bs WHERE bs."CompanyId"=b."CompanyId" ORDER BY bs."Id" LIMIT 1)))) AS gstin) x
  WHERE b."StateId" IS NULL AND pg_temp.geo_norm(b."State") IS NOT NULL),
ctx AS (
  SELECT "Id", n0, coalesce(gstin ~ '^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$'
                            AND substr(gstin,1,2) IN (SELECT prefix FROM _geo_gst), false) AS india_ctx FROM src),
b0 AS (   -- number-strip forms; num = the stripped 2-digit number
  SELECT "Id", india_ctx, n0 AS form, NULL::text AS num FROM ctx
  UNION ALL SELECT "Id", india_ctx, regexp_replace(n0,'^\d{2} ',''), substring(n0 from '^(\d{2}) ') FROM ctx WHERE n0 ~ '^\d{2} '
  UNION ALL SELECT "Id", india_ctx, regexp_replace(n0,' \(?\d{2}\)?$',''), substring(n0 from ' \(?(\d{2})\)?$') FROM ctx WHERE n0 ~ ' \(?\d{2}\)?$'),
forms AS (   -- suffix strip is applied after the number strip so the forms combine
  SELECT "Id", india_ctx, form, NULL::text AS cc, num FROM b0
  UNION ALL SELECT b0."Id", b0.india_ctx, left(b0.form, length(b0.form)-length(x.suffix)-1), x.cc, b0.num
            FROM b0 JOIN _geo_suffix x ON b0.form LIKE '% '||x.suffix),
hits AS (
  SELECT DISTINCT f."Id", c."StateId" FROM forms f
  JOIN _geo_cand c ON c.norm = f.form AND (f.cc IS NULL OR c.cc = f.cc) AND (NOT c.india_only OR f.india_ctx)
  JOIN "States" cs ON cs."Id" = c."StateId"
  WHERE f.form <> ''
    AND (f.num IS NULL OR c.cc <> 'IN' OR f.num NOT IN (SELECT prefix FROM _geo_gst)
         OR f.num = cs."GstStateCode" OR (f.num = '25' AND cs."Code" = 'DH'))),
uniq AS (
  SELECT "Id", (array_agg("StateId"))[1] AS "StateId" FROM hits GROUP BY "Id" HAVING count(DISTINCT "StateId") = 1)
UPDATE "Branches" b SET "StateId" = u."StateId", "CountryId" = st."CountryId"
FROM uniq u JOIN "States" st ON st."Id" = u."StateId" WHERE b."Id" = u."Id";

-- ===== Branches: pass 2 (GSTIN prefix; never overwrites a linked row) =====
-- own GSTIN always; the inherited company GSTIN only when the branch text is blank (D12)
WITH src AS (
  SELECT b."Id", upper(btrim(CASE
            WHEN nullif(btrim(b."Gstin"),'') IS NOT NULL THEN b."Gstin"
            WHEN regexp_replace(coalesce(b."State",''), '[\t\n\v\f\r \u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]+', '', 'g') = '' THEN
              (SELECT bs."Gstin" FROM "BusinessSettings" bs WHERE bs."CompanyId"=b."CompanyId" ORDER BY bs."Id" LIMIT 1) END)) AS gstin
  FROM "Branches" b WHERE b."StateId" IS NULL AND b."CountryId" IS NULL)
UPDATE "Branches" b SET "StateId" = g."StateId", "CountryId" = st."CountryId"
FROM src s JOIN _geo_gst g ON g.prefix = substr(s.gstin,1,2) JOIN "States" st ON st."Id" = g."StateId"
WHERE b."Id" = s."Id" AND s.gstin ~ '^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$';

-- ===== BusinessSettings: pass 1 (text) =====
WITH src AS (
  SELECT b."Id", pg_temp.geo_norm(b."State") AS n0, upper(btrim(b."Gstin")) AS gstin
  FROM "BusinessSettings" b
  WHERE b."StateId" IS NULL AND pg_temp.geo_norm(b."State") IS NOT NULL),
ctx AS (
  SELECT "Id", n0, coalesce(gstin ~ '^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$'
                            AND substr(gstin,1,2) IN (SELECT prefix FROM _geo_gst), false) AS india_ctx FROM src),
b0 AS (   -- number-strip forms; num = the stripped 2-digit number
  SELECT "Id", india_ctx, n0 AS form, NULL::text AS num FROM ctx
  UNION ALL SELECT "Id", india_ctx, regexp_replace(n0,'^\d{2} ',''), substring(n0 from '^(\d{2}) ') FROM ctx WHERE n0 ~ '^\d{2} '
  UNION ALL SELECT "Id", india_ctx, regexp_replace(n0,' \(?\d{2}\)?$',''), substring(n0 from ' \(?(\d{2})\)?$') FROM ctx WHERE n0 ~ ' \(?\d{2}\)?$'),
forms AS (   -- suffix strip is applied after the number strip so the forms combine
  SELECT "Id", india_ctx, form, NULL::text AS cc, num FROM b0
  UNION ALL SELECT b0."Id", b0.india_ctx, left(b0.form, length(b0.form)-length(x.suffix)-1), x.cc, b0.num
            FROM b0 JOIN _geo_suffix x ON b0.form LIKE '% '||x.suffix),
hits AS (
  SELECT DISTINCT f."Id", c."StateId" FROM forms f
  JOIN _geo_cand c ON c.norm = f.form AND (f.cc IS NULL OR c.cc = f.cc) AND (NOT c.india_only OR f.india_ctx)
  JOIN "States" cs ON cs."Id" = c."StateId"
  WHERE f.form <> ''
    AND (f.num IS NULL OR c.cc <> 'IN' OR f.num NOT IN (SELECT prefix FROM _geo_gst)
         OR f.num = cs."GstStateCode" OR (f.num = '25' AND cs."Code" = 'DH'))),
uniq AS (
  SELECT "Id", (array_agg("StateId"))[1] AS "StateId" FROM hits GROUP BY "Id" HAVING count(DISTINCT "StateId") = 1)
UPDATE "BusinessSettings" b SET "StateId" = u."StateId", "CountryId" = st."CountryId"
FROM uniq u JOIN "States" st ON st."Id" = u."StateId" WHERE b."Id" = u."Id";

-- ===== BusinessSettings: pass 2 (GSTIN prefix; never overwrites a linked row) =====
-- own GSTIN only (no company fallback)
WITH src AS (
  SELECT b."Id", upper(btrim(b."Gstin")) AS gstin
  FROM "BusinessSettings" b WHERE b."StateId" IS NULL AND b."CountryId" IS NULL)
UPDATE "BusinessSettings" b SET "StateId" = g."StateId", "CountryId" = st."CountryId"
FROM src s JOIN _geo_gst g ON g.prefix = substr(s.gstin,1,2) JOIN "States" st ON st."Id" = g."StateId"
WHERE b."Id" = s."Id" AND s.gstin ~ '^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$';

-- ===== Suppliers: pass 2 (GSTIN prefix; never overwrites a linked row) =====
-- own GSTIN only (no company fallback)
WITH src AS (
  SELECT b."Id", upper(btrim(b."Gstin")) AS gstin
  FROM "Suppliers" b WHERE b."StateId" IS NULL AND b."CountryId" IS NULL)
UPDATE "Suppliers" b SET "StateId" = g."StateId", "CountryId" = st."CountryId"
FROM src s JOIN _geo_gst g ON g.prefix = substr(s.gstin,1,2) JOIN "States" st ON st."Id" = g."StateId"
WHERE b."Id" = s."Id" AND s.gstin ~ '^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$';
