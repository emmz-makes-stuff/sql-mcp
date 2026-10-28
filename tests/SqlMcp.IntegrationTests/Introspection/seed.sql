-- A database holding every object type the introspection tools cover, plus the awkward cases: an encrypted
-- procedure, a database user, a database DDL trigger, a temporal table, a partitioned table, and the name clashes at
-- the end. Names are written in one consistent case, so the seed also runs on a case-sensitive collation.

CREATE SCHEMA sales;
GO
CREATE SCHEMA archive;
GO
CREATE TYPE dbo.phone FROM varchar(20) NOT NULL;
GO
CREATE TYPE dbo.id_list AS TABLE (id int NOT NULL PRIMARY KEY);
GO
CREATE SEQUENCE dbo.order_numbers AS bigint START WITH 1000 INCREMENT BY 10 MINVALUE 1000 MAXVALUE 999999 NO CYCLE;
GO
CREATE TABLE dbo.users (
    id int IDENTITY(1, 1) NOT NULL CONSTRAINT pk_users PRIMARY KEY,
    email nvarchar(200) NOT NULL CONSTRAINT uq_users_email UNIQUE,
    phone dbo.phone NULL,
    created_at datetime2(3) NOT NULL CONSTRAINT df_users_created DEFAULT (sysutcdatetime()),
    display_name AS (lower(email)),
    CONSTRAINT ck_users_email CHECK (email LIKE '%@%')
);
GO
INSERT INTO dbo.users (email) VALUES (N'a@example.com'), (N'b@example.com'), (N'c@example.com');
GO
CREATE TABLE dbo.orders (
    id int IDENTITY(1, 1) NOT NULL CONSTRAINT pk_orders PRIMARY KEY,
    user_id int NOT NULL CONSTRAINT fk_orders_users REFERENCES dbo.users (id),
    number bigint NOT NULL CONSTRAINT df_orders_number DEFAULT (NEXT VALUE FOR dbo.order_numbers),
    placed_at date NOT NULL,
    status varchar(10) NOT NULL
);
GO
CREATE INDEX ix_orders_user ON dbo.orders (user_id) INCLUDE (placed_at);
GO
CREATE TABLE dbo.order_lines (
    order_id int NOT NULL CONSTRAINT fk_order_lines_orders REFERENCES dbo.orders (id),
    line int NOT NULL,
    quantity int NOT NULL,
    CONSTRAINT pk_order_lines PRIMARY KEY (order_id, line)
);
GO
CREATE TABLE archive.orders (
    id int NOT NULL CONSTRAINT pk_archive_orders PRIMARY KEY,
    placed_at date NOT NULL
);
GO
CREATE TABLE dbo.Order_Archive (id int NOT NULL CONSTRAINT pk_order_archive PRIMARY KEY);
GO
CREATE TABLE dbo.prices (
    id int NOT NULL CONSTRAINT pk_prices PRIMARY KEY,
    amount decimal(10, 2) NOT NULL,
    valid_from datetime2 GENERATED ALWAYS AS ROW START NOT NULL,
    valid_to datetime2 GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME (valid_from, valid_to)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.prices_history));
GO
CREATE PARTITION FUNCTION pf_events (int) AS RANGE RIGHT FOR VALUES (100, 200);
GO
CREATE PARTITION SCHEME ps_events AS PARTITION pf_events ALL TO ([PRIMARY]);
GO
CREATE TABLE dbo.events (id int NOT NULL, CONSTRAINT pk_events PRIMARY KEY (id)) ON ps_events (id);
GO
CREATE VIEW sales.open_orders AS
SELECT o.id, o.user_id, o.placed_at FROM dbo.orders AS o WHERE o.status = 'open';
GO
CREATE PROCEDURE dbo.archive_orders @before date AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO archive.orders (id, placed_at) SELECT id, placed_at FROM dbo.orders WHERE placed_at < @before;
END;
GO
CREATE PROCEDURE dbo.secret_report WITH ENCRYPTION AS
SELECT COUNT(*) AS n FROM dbo.users;
GO
CREATE FUNCTION dbo.order_count (@user_id int) RETURNS int AS
BEGIN
    RETURN (SELECT COUNT(*) FROM dbo.orders WHERE user_id = @user_id);
END;
GO
CREATE FUNCTION dbo.orders_for (@user_id int) RETURNS TABLE AS
RETURN (SELECT id, placed_at FROM dbo.orders WHERE user_id = @user_id);
GO
CREATE TRIGGER dbo.orders_status ON dbo.orders AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
END;
GO
CREATE SYNONYM dbo.customers FOR dbo.users;
GO
CREATE TRIGGER audit_ddl ON DATABASE FOR CREATE_PROCEDURE AS
BEGIN
    SET NOCOUNT ON;
END;
GO
CREATE USER app_reader WITHOUT LOGIN;
GO
-- A table and a table type sharing one name (types are a namespace of their own), and two foreign keys sharing one
-- name in different schemas, both referencing archive.orders.
CREATE SCHEMA crm;
GO
CREATE TYPE crm.address AS TABLE (line nvarchar(100) NOT NULL);
GO
CREATE TABLE crm.address (id int NOT NULL CONSTRAINT pk_crm_address PRIMARY KEY, line nvarchar(100) NOT NULL);
GO
CREATE TABLE crm.returns (order_id int NOT NULL CONSTRAINT fk_returns_orders REFERENCES archive.orders (id));
GO
CREATE TABLE archive.returns (order_id int NOT NULL CONSTRAINT fk_returns_orders REFERENCES archive.orders (id));
GO
