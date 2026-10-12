CREATE TABLE IF NOT EXISTS users (
    id INT PRIMARY KEY,
    login_id VARCHAR(64) NOT NULL UNIQUE,
    name VARCHAR(64) NOT NULL,
    score INT NOT NULL DEFAULT 1000
) ENGINE=InnoDB;
CREATE TABLE IF NOT EXISTS heroes (
    id BIGINT PRIMARY KEY,
    user_id INT NOT NULL,
    template_id INT NOT NULL,
    is_pinned BOOLEAN NOT NULL DEFAULT FALSE,
    is_locked BOOLEAN NOT NULL DEFAULT FALSE,
    INDEX ix_heroes_user (user_id),
    CONSTRAINT fk_heroes_user FOREIGN KEY (user_id) REFERENCES users(id)
) ENGINE=InnoDB;
