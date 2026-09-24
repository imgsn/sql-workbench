param([ValidateSet('setup','cleanup')][string]$Action)
$ErrorActionPreference='Stop'
$fixturePath=Join-Path $PSScriptRoot '.browser-fixture.json'
if ($Action -eq 'setup') {
    if (Test-Path -LiteralPath $fixturePath) { throw 'Clean up the previous browser fixture first.' }
    $suffix=[Guid]::NewGuid().ToString('N').Substring(0,10)
    $fixture=@{ source="Workbench_Browser_${suffix}_A"; target="Workbench_Browser_${suffix}_B"; login="Workbench_Browser_${suffix}"; password=('Wb!'+[Guid]::NewGuid().ToString('N')+'9') }
    $fixture | ConvertTo-Json | Set-Content -LiteralPath $fixturePath -Encoding utf8
    $sql=@"
CREATE DATABASE [$($fixture.source)];
CREATE DATABASE [$($fixture.target)];
CREATE LOGIN [$($fixture.login)] WITH PASSWORD='$($fixture.password)', CHECK_POLICY=OFF;
GO
"@
    foreach($name in @($fixture.source,$fixture.target)) {
      $sql+="`n"
      $sql+=@"
USE [$name];
GO
CREATE TABLE dbo.Customers (CustomerId int IDENTITY PRIMARY KEY, Name nvarchar(100) NOT NULL, City nvarchar(80) NULL, Amount decimal(18,2) NOT NULL);
INSERT dbo.Customers(Name,City,Amount) VALUES(N'O''Brien',N'Riyadh',10.25),(N'Customer two',NULL,20);
CREATE USER [$($fixture.login)] FOR LOGIN [$($fixture.login)];
GRANT SELECT, VIEW DEFINITION TO [$($fixture.login)];
DENY INSERT, UPDATE, DELETE TO [$($fixture.login)];
GO
"@
    }
    $sql+="`n"
    $sql+=@"
USE [$($fixture.target)];
ALTER TABLE dbo.Customers ALTER COLUMN Name nvarchar(80) NOT NULL;
UPDATE dbo.Customers SET City=N'Jeddah' WHERE CustomerId=1;
GO
"@
    $sql | sqlcmd -S localhost -E -C -b -l 5 -o (Join-Path $PSScriptRoot '.browser-fixture.log')
    if($LASTEXITCODE -ne 0){throw 'Browser fixture setup failed; see local fixture log.'}
    $source="Server=localhost;Database=$($fixture.source);User Id=$($fixture.login);Password=$($fixture.password);TrustServerCertificate=True"
    $target="Server=localhost;Database=$($fixture.target);User Id=$($fixture.login);Password=$($fixture.password);TrustServerCertificate=True"
    $template=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'live-smoke.template.js') -Raw
    $rendered=$template.Replace('__SOURCE_CONNECTION__',($source|ConvertTo-Json -Compress)).Replace('__TARGET_CONNECTION__',($target|ConvertTo-Json -Compress))
    Set-Content -LiteralPath (Join-Path $PSScriptRoot '.live-smoke.js') -Value $rendered -Encoding utf8
    Write-Output 'Temporary browser fixtures ready.'
} else {
    if (!(Test-Path -LiteralPath $fixturePath)) { return }
    $fixture=Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json
    foreach($name in @($fixture.source,$fixture.target)) {
        if($name -notmatch '^Workbench_Browser_[a-f0-9]{10}_[AB]$'){throw 'Unsafe cleanup target'}
        "IF DB_ID(N'$name') IS NOT NULL BEGIN ALTER DATABASE [$name] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$name]; END" | sqlcmd -S localhost -E -C -b -o (Join-Path $PSScriptRoot '.browser-fixture.log')
        if($LASTEXITCODE -ne 0){throw 'Database cleanup failed'}
    }
    if($fixture.login -notmatch '^Workbench_Browser_[a-f0-9]{10}$'){throw 'Unsafe cleanup login'}
    "IF SUSER_ID(N'$($fixture.login)') IS NOT NULL DROP LOGIN [$($fixture.login)];" | sqlcmd -S localhost -E -C -b -o (Join-Path $PSScriptRoot '.browser-fixture.log')
    if($LASTEXITCODE -ne 0){throw 'Login cleanup failed'}
    Remove-Item -LiteralPath $fixturePath,(Join-Path $PSScriptRoot '.live-smoke.js') -Force -ErrorAction SilentlyContinue
    Write-Output 'Temporary browser fixtures removed.'
}
