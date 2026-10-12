-- Synthetic portfolio data; no production accounts or game balance data.
INSERT IGNORE INTO users(id,login_id,name,score) VALUES
    (1,'demo-a','Demo A',1000), (2,'demo-b','Demo B',1050);
INSERT IGNORE INTO heroes(id,user_id,template_id,is_pinned,is_locked) VALUES
    (101,1,1,FALSE,FALSE), (102,1,2,FALSE,TRUE), (201,2,1,FALSE,FALSE);
