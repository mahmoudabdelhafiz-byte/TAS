-- 012: questions and tokens per user and day for the AI assistant (daily limit, cost). No conversation text is stored.

CREATE TABLE IF NOT EXISTS assistant_usage (
    user_id        integer  NOT NULL REFERENCES app_user(user_id) ON DELETE CASCADE,
    day            date     NOT NULL,
    questions      integer  NOT NULL DEFAULT 0,
    input_tokens   bigint   NOT NULL DEFAULT 0,
    output_tokens  bigint   NOT NULL DEFAULT 0,
    PRIMARY KEY (user_id, day)
);
