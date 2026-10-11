-- Optional multilingual gazetteer. Explicit mentions are weak evidence, never final country assignment.
CREATE TABLE IF NOT EXISTS news_country_aliases (
  country_profile_id integer NOT NULL REFERENCES "Countries"("Id") ON DELETE CASCADE,
  alias text NOT NULL,
  language varchar(12) NOT NULL,
  is_ambiguous boolean NOT NULL DEFAULT false,
  PRIMARY KEY (country_profile_id, alias, language)
);
CREATE INDEX IF NOT EXISTS ix_news_country_aliases_country ON news_country_aliases(country_profile_id);

-- The country catalog remains authoritative; never create new countries here.
-- Add non-English spellings conservatively; avoid two-letter abbreviations.
WITH aliases(country_name, alias, language, ambiguous) AS (
  VALUES
    ('Russia','Россия','ru',false), ('Russia','Российская Федерация','ru',false),
    ('China','Китай','ru',false), ('China','中国','zh',false),
    ('United States','США','ru',false), ('United States','Соединенные Штаты','ru',false),
    ('United Kingdom','Великобритания','ru',false),
    ('Germany','Германия','ru',false), ('Germany','Deutschland','de',false),
    ('France','Франция','ru',false),
    ('India','Индия','ru',false), ('India','भारत','hi',false),
    ('Japan','Япония','ru',false), ('Japan','日本','ja',false),
    ('South Korea','Южная Корея','ru',false), ('South Korea','대한민국','ko',false),
    ('Kazakhstan','Казахстан','ru',false), ('Kazakhstan','Қазақстан','kk',false),
    ('Kyrgyzstan','Кыргызстан','ru',false), ('Kyrgyzstan','Кыргыз Республикасы','ky',false),
    ('Uzbekistan','Узбекистан','ru',false), ('Uzbekistan','Oʻzbekiston','uz',false),
    ('Turkey','Турция','ru',false), ('Turkey','Türkiye','tr',false),
    ('Ukraine','Украина','ru',false), ('Ukraine','Україна','uk',false),
    ('Iran','Иран','ru',false), ('Iran','ایران','fa',false),
    ('Iraq','Ирак','ru',false), ('Iraq','العراق','ar',false),
    ('Israel','Израиль','ru',false), ('Israel','ישראל','he',false),
    ('Egypt','Египет','ru',false), ('Egypt','مصر','ar',false),
    ('Brazil','Бразилия','ru',false), ('Brazil','Brasil','pt',false),
    ('Georgia','Грузия','ru',false), ('Georgia','Georgia','en',true)
)
INSERT INTO news_country_aliases (country_profile_id,alias,language,is_ambiguous)
SELECT c."Id", a.alias, a.language, a.ambiguous
FROM aliases a JOIN "Countries" c ON c."Name"=a.country_name
ON CONFLICT DO NOTHING;
