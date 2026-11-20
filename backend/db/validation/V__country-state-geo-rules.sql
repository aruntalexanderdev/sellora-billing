-- sellora:description Geography rules that SQL cannot enforce (GST state code only on Indian states, BR4)
-- sellora:on-violation warn
SELECT s."Id", 'gst code on non-IN state' AS issue FROM "States" s JOIN "Countries" c ON c."Id"=s."CountryId"
WHERE s."GstStateCode" IS NOT NULL AND c."Code" <> 'IN';
