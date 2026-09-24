// Deliberately fictitious fixtures. This prototype has no database access.
window.workbenchDemo = (() => {
    const column = (name, type, nullable = false, extra = '') => ({ name, type, nullable, extra });
    const tables = {
        'dbo.Customers': [column('CustomerId', 'int', false, 'PRIMARY KEY'), column('Name', 'nvarchar(100)'), column('Email', 'nvarchar(255)'), column('City', 'nvarchar(80)', true), column('IsActive', 'bit'), column('CreatedAt', 'datetime2')],
        'dbo.Orders': [column('OrderId', 'int', false, 'PRIMARY KEY'), column('CustomerId', 'int'), column('Total', 'decimal(18,2)'), column('Status', 'nvarchar(30)'), column('CreatedAt', 'datetime2')],
        'dbo.OrderItems': [column('OrderItemId', 'int', false, 'PRIMARY KEY'), column('OrderId', 'int'), column('ProductId', 'int'), column('Quantity', 'int')],
        'dbo.Products': [column('ProductId', 'int', false, 'PRIMARY KEY'), column('Name', 'nvarchar(120)'), column('Price', 'decimal(18,2)'), column('Sku', 'nvarchar(40)')],
        'dbo.Categories': [column('CategoryId', 'int', false, 'PRIMARY KEY'), column('Name', 'nvarchar(80)')],
        'dbo.Addresses': [column('AddressId', 'int', false, 'PRIMARY KEY'), column('CustomerId', 'int'), column('Line1', 'nvarchar(200)')],
        'dbo.Payments': [column('PaymentId', 'int', false, 'PRIMARY KEY'), column('OrderId', 'int'), column('Amount', 'decimal(18,2)')],
        'audit.ActivityLog': [column('LogId', 'bigint', false, 'PRIMARY KEY'), column('Action', 'nvarchar(200)'), column('CreatedAt', 'datetime2')]
    };
    const staging = JSON.parse(JSON.stringify(tables));
    staging['dbo.Customers'][2].type = 'nvarchar(150)';
    staging['dbo.Customers'][3].nullable = false;
    staging['dbo.Orders'][2].type = 'decimal(12,2)';
    staging['dbo.Products'].pop();
    delete staging['audit.ActivityLog'];
    staging['dbo.LegacyCustomers'] = [column('Id', 'int', false, 'PRIMARY KEY'), column('Name', 'varchar(80)')];
    const rows = [
        { CustomerId: 1001, Name: 'Nora Al-Harbi', Email: 'nora@example.test', City: 'Riyadh', IsActive: true, CreatedAt: '2026-08-12T09:00:00' },
        { CustomerId: 1002, Name: 'Omar Hassan', Email: 'omar@example.test', City: 'Jeddah', IsActive: true, CreatedAt: '2026-08-14T10:30:00' },
        { CustomerId: 1003, Name: 'Layla Saleh', Email: 'layla@example.test', City: 'Dammam', IsActive: true, CreatedAt: '2026-08-16T11:00:00' },
        { CustomerId: 1004, Name: "Sam O'Brien", Email: 'sam@example.test', City: null, IsActive: false, CreatedAt: '2026-08-19T13:15:00' },
        { CustomerId: 1005, Name: 'سارة أحمد', Email: 'sara@example.test', City: 'Riyadh', IsActive: true, CreatedAt: '2026-08-20T08:00:00' },
        { CustomerId: 1006, Name: 'Yousef Ali', Email: 'yousef@example.test', City: 'Abha', IsActive: true, CreatedAt: '2026-08-21T14:00:00' },
        { CustomerId: 1007, Name: 'Maha Khalid', Email: 'maha@example.test', City: 'Jeddah', IsActive: false, CreatedAt: '2026-08-22T09:00:00' }
    ];
    const targetRows = JSON.parse(JSON.stringify(rows.slice(0, 6)));
    targetRows[1].City = 'Riyadh';
    targetRows[2].IsActive = false;
    targetRows.push({ CustomerId: 1008, Name: 'Faisal Ahmed', Email: 'faisal@example.test', City: 'Tabuk', IsActive: true, CreatedAt: '2026-08-23T09:00:00' });
    return {
        connections: [{ id: 'dev', name: 'Commerce Development', server: 'sql-dev.internal', database: 'Commerce', environment: 'Development', version: 'SQL Server 2022' }, { id: 'stage', name: 'Commerce Staging', server: 'sql-stage.internal', database: 'Commerce', environment: 'Staging', version: 'SQL Server 2019' }],
        schemas: { dev: tables, stage: staging }, rows: { dev: rows, stage: targetRows }
    };
})();
