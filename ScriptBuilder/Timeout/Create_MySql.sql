﻿set @fullTableName = concat(@schema, '.', @tablePrefix, 'TimeoutData');
set @createTable = concat('
    create table if not exists ', @fullTableName, '(
        Id varchar(38) not null,
        Destination varchar(1024),
        SagaId varchar(38),
        State longblob,
        Time datetime,
        Headers longtext not null,
        PersistenceVersion varchar(23) not null,
        primary key (`Id`)
    ) DEFAULT CHARSET=utf8;
');
prepare statment from @createTable;
execute statment;
deallocate prepare statment;