#Requires -Version 7.4
param(
    [Parameter(Mandatory)] [string] $PostgresBin,
    [Parameter(Mandatory)] [string] $AgeBin,
    [Parameter(Mandatory)] [string] $EvidencePath
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = Split-Path -Parent $PSScriptRoot
$local = Join-Path $repo '.local'
$id = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$network = "td-release-drill-$id"
$work = Join-Path $local $network
$pg = "$network-pg"
$prev = "$network-prev"
$current = "$network-current"
$restored = "$network-restore"
$fixture = "$network-github"
$containers = [System.Collections.Generic.List[string]]::new()
$sourceDb = 'td_release_drill'
$targetDb = 'td_recovery_drill'
$earlierMigration = '20261125115020_IssueHandoff'
$candidate = (docker image inspect triage-desk-slice8-candidate:local --format '{{.Id}}').Trim()
$previous = (docker image inspect triage-desk-slice8-previous:local --format '{{.Id}}').Trim()
$stamp = (Get-Content (Join-Path $local 'slice-8-source-stamp.txt') -Raw).Trim()
$adminPassword = [Guid]::NewGuid().ToString('N')
$migrationPassword = [Guid]::NewGuid().ToString('N')
$runtimePassword = [Guid]::NewGuid().ToString('N')
$backupPassword = [Guid]::NewGuid().ToString('N')
$accountPassword = 'local synthetic recovery test password'

function Assert-Drill($condition, [string] $message) {
    if (-not $condition) { throw $message }
}

function Native-Ok([string] $operation) {
    if ($LASTEXITCODE -ne 0) { throw "$operation returned exit code $LASTEXITCODE." }
}

function Use-Db($database, $user, $password) {
    $env:PGHOST = '127.0.0.1'
    $env:PGPORT = '55438'
    $env:PGDATABASE = $database
    $env:PGUSER = $user
    $env:PGPASSWORD = $password
}

function Query([string] $sql) {
    $result = $sql | & (Join-Path $PostgresBin 'psql.exe') -X -qAt -v ON_ERROR_STOP=1
    Native-Ok 'PostgreSQL command'
    $result
}

function Upgrade([string] $database, [string] $target = '') {
    $env:ConnectionStrings__TriageDesk = "Host=127.0.0.1;Port=55438;Database=$database;Username=td_migrate;Password=$migrationPassword"
    $arguments = @('ef','database','update','--project','apps/api/TriageDesk.Api','--no-build')
    if ($target) { $arguments += $target }
    dotnet @arguments *>> (Join-Path $work 'migrations.log')
    Native-Ok 'EF migration'
}

function Grants {
    $arguments = @('-X','-q','-f',(Join-Path $PSScriptRoot 'database/roles.sql'),
        '-v','migration_role=td_migrate','-v','runtime_role=td_runtime','-v','backup_role=td_backup')
    & (Join-Path $PostgresBin 'psql.exe') @arguments
    Native-Ok 'Runtime and backup grants'
}

function Wait-Http([int] $port, [string] $path, [int] $status) {
    for ($i = 0; $i -lt 30; $i++) {
        try {
            $r = Invoke-WebRequest -Uri "https://localhost:$port$path" -SkipCertificateCheck -SkipHttpErrorCheck -TimeoutSec 3
            if ($r.StatusCode -eq $status) { return $r }
        } catch { }
        Start-Sleep -Seconds 1
    }
    throw "Expected HTTP $status at local port $port for $path."
}

function Start-Api($name, $port, $image, $database, [switch] $baseline, [switch] $quarantine) {
    $env:ConnectionStrings__TriageDesk = "Host=$pg;Database=$database;Username=td_runtime;Password=$runtimePassword;Maximum Pool Size=10"
    $env:Operations__RecoveryMode = if ($quarantine) { 'true' } else { 'false' }
    $arguments = @('run','-d','--name',$name,'--network',$network,'-p',"127.0.0.1:$($port):10000",
        '--mount',"type=bind,src=$tls,dst=/tls,readonly",
        '-e','ConnectionStrings__TriageDesk','-e','ASPNETCORE_ENVIRONMENT=Development',
        '-e','ASPNETCORE_URLS=https://+:10000','-e','Kestrel__Certificates__Default__Path=/tls/localhost.pem',
        '-e','Kestrel__Certificates__Default__KeyPath=/tls/localhost.key','-e','Identity__InboxDirectory=/tmp/td-inbox',
        '-e','Identity__WebOrigin=https://localhost:3000','-e','Operations__RecoveryMode')
    if (-not $baseline) {
        $arguments += @('-e','Identity__KeyCertificate','-e','Identity__KeyCertificatePassword',
            '-e','Operations__StructuredLogs=true','-e','GitHub__FixtureOrigin=http://127.0.0.1:5101')
    }
    docker @arguments $image | Out-Null
    Native-Ok 'Start API container'
    $containers.Add($name)
}

function Api($port, $session, $method, $path, $body = $null, [int] $expected = 200) {
    $request = @{ Uri = "https://localhost:$port/api/v1$path"; Method = $method; WebSession = $session
        SkipCertificateCheck = $true; SkipHttpErrorCheck = $true; TimeoutSec = 20 }
    if ($method -ne 'GET') {
        $csrf = Api $port $session GET '/auth/csrf'
        $request.Headers = @{ 'X-CSRF-Token' = $csrf.requestToken }
    }
    if ($null -ne $body) {
        $request.Body = ConvertTo-Json -Compress -Depth 8 -InputObject $body
        $request.ContentType = 'application/json'
    }
    $r = Invoke-WebRequest @request
    Assert-Drill ($r.StatusCode -eq $expected) "$method $path returned $($r.StatusCode), expected $expected."
    if ($r.Content) { $r.Content | ConvertFrom-Json }
}

function Session { [Microsoft.PowerShell.Commands.WebRequestSession]::new() }

function Sign-In($port, $session, [string] $email) {
    Api $port $session POST '/auth/login' @{ email = $email; password = $accountPassword } 204 | Out-Null
    Api $port $session GET '/me'
}

function Register-Verified($port, $container, $session, [string] $email, [string] $name) {
    Api $port $session POST '/auth/register' @{ email = $email; password = $accountPassword; displayName = $name } 202 | Out-Null
    $key = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($email.ToUpperInvariant()))).ToLowerInvariant()
    $message = docker exec $container cat "/tmp/td-inbox/$key.json" | ConvertFrom-Json
    Native-Ok 'Read synthetic verification mail'
    $parts = @{}
    foreach ($part in ([Uri] $message.url).Fragment.Substring(1).Split('&')) {
        $pair = $part.Split('=', 2)
        $parts[$pair[0]] = [Uri]::UnescapeDataString($pair[1])
    }
    Api $port $session POST '/auth/email-verification' @{ userId = $parts.userId; token = $parts.token } 204 | Out-Null
    Sign-In $port $session $email
}

Push-Location $repo
try {
    $tls = Join-Path $work 'tls'
    New-Item -ItemType Directory -Force -Path $tls | Out-Null
    Copy-Item -LiteralPath (Join-Path $local 'localhost.pem'), (Join-Path $local 'localhost.key') -Destination $tls
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        'CN=Triage Desk local rehearsal', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(2))
    $env:Identity__KeyCertificatePassword = [Guid]::NewGuid().ToString('N')
    $env:Identity__KeyCertificate = [Convert]::ToBase64String($certificate.Export(
        [Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12, $env:Identity__KeyCertificatePassword))
    $env:POSTGRES_PASSWORD = $adminPassword
    docker network create $network | Out-Null
    Native-Ok 'Create isolated Docker network'
    docker run -d --name $pg --network $network -e POSTGRES_PASSWORD -e "POSTGRES_DB=$sourceDb" -p 127.0.0.1:55438:5432 postgres:18 | Out-Null
    Native-Ok 'Start disposable PostgreSQL'
    $containers.Add($pg)
    for ($i = 0; $i -lt 40; $i++) {
        docker exec $pg pg_isready -h 127.0.0.1 -U postgres -d $sourceDb 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Seconds 1
    }
    Native-Ok 'Wait for disposable PostgreSQL'
    Use-Db $sourceDb postgres $adminPassword
    Query @"
CREATE ROLE td_migrate LOGIN PASSWORD '$migrationPassword';
CREATE ROLE td_runtime LOGIN PASSWORD '$runtimePassword';
CREATE ROLE td_backup LOGIN PASSWORD '$backupPassword';
ALTER SCHEMA public OWNER TO td_migrate;
"@ | Out-Null
    Upgrade $sourceDb $earlierMigration
    Grants
    Start-Api $prev 55100 $previous $sourceDb -Baseline
    Wait-Http 55100 '/api/v1/auth/csrf' 200 | Out-Null
    $oldOwner = Session
    $oldMember = Session
    $ownerEmail = "owner-$id@example.test"
    $memberEmail = "member-$id@example.test"
    Register-Verified 55100 $prev $oldOwner $ownerEmail 'Restore Owner' | Out-Null
    $member = Register-Verified 55100 $prev $oldMember $memberEmail 'Restore Member'
    $workspace = (Api 55100 $oldOwner POST '/workspaces' @{ name = 'Synthetic restore drill' } 201).workspaceId
    $workspaceRoute = "/workspaces/$workspace"
    $invite = Api 55100 $oldOwner POST "$workspaceRoute/invitations" @{ email = $memberEmail; role = 'member' } 201
    Api 55100 $oldMember POST '/invitations/accept' @{ token = ([Uri] $invite.acceptUrl).Fragment.Substring(7) } | Out-Null
    $repository = [Guid]::NewGuid().ToString()
    $issue = [Guid]::NewGuid().ToString()
    $issueRoute = "$workspaceRoute/issues/$issue"
    Query @"
INSERT INTO "Repositories" ("WorkspaceId","Id","GitHubId","FullName","ConnectedAt","Public")
VALUES ('$workspace','$repository',801,'northline/cli',now(),true);
INSERT INTO "IssueSnapshots" ("WorkspaceId","Id","RepositoryId","GitHubId","Number","Title","Body","State","Labels",
    "SourceAvailable","SourceCreatedAt","SourceUpdatedAt","ObservedAt")
VALUES ('$workspace','$issue','$repository',201,42,'CLI crashes on Windows','Synthetic report','open',ARRAY['bug'],
    true,'2026-10-11T09:00:00Z','2026-10-12T09:00:00Z',now());
INSERT INTO "IssueTriage" ("WorkspaceId","IssueId","Status","EnteredReviewAt","UpdatedAt","DecisionVersion")
VALUES ('$workspace','$issue','needsReview',now(),now(),1);
"@ | Out-Null
    Api 55100 $oldOwner POST "$issueRoute/commands" @{ kind = 'accept'; expectedVersion = 1
        commandId = [Guid]::NewGuid(); priority = 'normal'; nextStep = 'Retain this decision' } | Out-Null
    docker stop $prev | Out-Null
    Native-Ok 'Stop previous API before migration'
    Upgrade $sourceDb
    Grants
    Start-Api $current 55101 $candidate $sourceDb
    $ready = Wait-Http 55101 '/health/ready' 204
    Assert-Drill (@($ready.Headers['X-TriageDesk-Release'])[0] -eq $stamp) 'Candidate readiness returned an unexpected release stamp.'
    $sourceOwner = Session
    Sign-In 55101 $sourceOwner $ownerEmail | Out-Null
    Assert-Drill ((Api 55101 $sourceOwner GET $issueRoute).triage.nextStep -eq 'Retain this decision') 'Upgrade lost the prior decision.'
    Api 55101 $sourceOwner POST "$issueRoute/commands" @{ kind = 'changePriority'; expectedVersion = 2
        commandId = [Guid]::NewGuid(); priority = 'high' } | Out-Null
    $runsRoute = "$workspaceRoute/repositories/$repository/sync-runs"
    $run = Api 55101 $sourceOwner POST $runsRoute @{ runId = [Guid]::NewGuid() } 201
    $pending = Api 55101 $sourceOwner POST "$workspaceRoute/invitations" @{ email = "pending-$id@example.test"; role = 'member' } 201

    Use-Db $sourceDb td_runtime $runtimePassword
    Assert-Drill ((Query 'SELECT has_schema_privilege(current_user, ''public'', ''CREATE''), has_table_privilege(current_user, ''"TeamAudit"'', ''DELETE''), has_table_privilege(current_user, ''"ProtectionKeys"'', ''DELETE'');') -eq 'f|f|f') 'Runtime grants are too broad.'
    Assert-Drill ([int](Query 'SELECT count(*) FROM "ProtectionKeys" WHERE "Xml" LIKE ''%encryptedSecret%'';') -ge 1) 'The database has no certificate-wrapped key.'
    Use-Db $sourceDb td_backup $backupPassword
    Assert-Drill ((Query 'SELECT has_table_privilege(current_user, ''"IssueTriage"'', ''SELECT''), has_table_privilege(current_user, ''"IssueTriage"'', ''UPDATE'');') -eq 't|f') 'Backup role is not read-only.'
    $snapshot = [DateTimeOffset]::Parse((Query 'SELECT clock_timestamp()'))
    $dump = Join-Path $work 'source.dump'
    & (Join-Path $PostgresBin 'pg_dump.exe') --format=custom --no-owner --no-privileges "--file=$dump"
    Native-Ok 'Export from backup role'
    $keyFile = Join-Path $work 'drill.age.key'
    $age = Join-Path $AgeBin 'age.exe'
    $keygen = Join-Path $AgeBin 'age-keygen.exe'
    & $keygen -o $keyFile 2> (Join-Path $work 'keygen.log')
    Native-Ok 'Generate a local backup key'
    $recipient = & $keygen -y $keyFile
    Native-Ok 'Read age public recipient'
    $ciphertext = Join-Path $work 'independent.dump.age'
    & $age --encrypt --recipient $recipient --output $ciphertext $dump
    Native-Ok 'Encrypt backup'
    $ciphertextHash = (Get-FileHash -LiteralPath $ciphertext).Hash
    $archiveHash = (Get-FileHash -LiteralPath $dump).Hash
    Remove-Item -LiteralPath $dump

    Api 55101 $sourceOwner DELETE "$workspaceRoute/members/$($member.userId)" $null 204 | Out-Null
    Api 55101 $sourceOwner DELETE "$workspaceRoute/invitations/$($pending.invitation.invitationId)" $null 204 | Out-Null
    $event = docker logs $current 2>$null | ForEach-Object { $_ | ConvertFrom-Json -ErrorAction SilentlyContinue } |
        Where-Object { $_.State.ActionName -eq 'member_removed' -and $_.State.WorkspaceId -eq $workspace } | Select-Object -Last 1
    Assert-Drill ($event.State.SubjectId -eq $member.userId) 'Independent audit output did not identify the removal.'
    $auditFile = Join-Path $work 'external-admin-event.json'
    $event.State | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $auditFile

    docker start $prev | Out-Null
    Native-Ok 'Roll back to previous image'
    Wait-Http 55100 '/api/v1/auth/csrf' 200 | Out-Null
    $rollback = Session
    Sign-In 55100 $rollback $ownerEmail | Out-Null
    $rolledBack = Api 55100 $rollback GET $issueRoute
    $history = Api 55100 $rollback GET "$issueRoute/activity"
    Assert-Drill ($rolledBack.triage.priority -eq 'high' -and $history.total -eq 2) 'Previous image could not read candidate decisions.'

    $detected = [DateTimeOffset]::UtcNow
    Assert-Drill ((Get-FileHash -LiteralPath $ciphertext).Hash -eq $ciphertextHash) 'Ciphertext checksum mismatch.'
    $readback = Join-Path $work 'restored.dump'
    & $age --decrypt --identity $keyFile --output $readback $ciphertext
    Native-Ok 'Decrypt backup with isolated identity'
    Assert-Drill ((Get-FileHash -LiteralPath $readback).Hash -eq $archiveHash) 'Archive checksum mismatch.'
    Use-Db $sourceDb postgres $adminPassword
    Query "CREATE DATABASE $targetDb OWNER td_migrate;" | Out-Null
    Use-Db $targetDb td_migrate $migrationPassword
    & (Join-Path $PostgresBin 'pg_restore.exe') --exit-on-error --single-transaction --no-owner --no-privileges "--dbname=$targetDb" $readback
    Native-Ok 'Restore archive into an isolated database'
    Remove-Item -LiteralPath $readback
    & (Join-Path $PostgresBin 'psql.exe') -X -q -f (Join-Path $PSScriptRoot 'database/quarantine-restore.sql')
    Native-Ok 'Quarantine restored access'
    # The synthetic fixture has no active assignment to retire.
    $replayed = Get-Content -LiteralPath $auditFile -Raw | ConvertFrom-Json
    $removedUser = [Guid] $replayed.SubjectId
    $removedWorkspace = [Guid] $replayed.WorkspaceId
    Assert-Drill ((Query "SELECT count(*) FROM ""IssueTriage"" WHERE ""WorkspaceId""='$removedWorkspace' AND ""AssigneeId""='$removedUser' AND ""Status"" IN ('ready','inProgress','waitingForInformation');") -eq '0') 'Drill fixture unexpectedly has an assignment to retire.'
    Query "UPDATE ""WorkspaceMembers"" SET ""RemovedAt""=clock_timestamp(), ""UpdatedAt""=clock_timestamp() WHERE ""WorkspaceId""='$removedWorkspace' AND ""UserId""='$removedUser';" | Out-Null
    Use-Db $targetDb postgres $adminPassword
    Grants
    Start-Api $restored 55102 $candidate $targetDb -Quarantine
    Wait-Http 55102 '/health/live' 204 | Out-Null
    Wait-Http 55102 '/health/ready' 503 | Out-Null
    Api 55102 (Session) POST '/auth/login' @{ email = $ownerEmail; password = $accountPassword } 503 | Out-Null
    docker rm -f $restored | Out-Null
    Native-Ok 'Restart candidate after isolation checks'
    $null = $containers.Remove($restored)
    Start-Api $restored 55102 $candidate $targetDb
    Wait-Http 55102 '/health/ready' 204 | Out-Null
    Api 55102 $sourceOwner GET '/me' $null 401 | Out-Null
    $restoreOwner = Session
    $restoreMember = Session
    Sign-In 55102 $restoreOwner $ownerEmail | Out-Null
    Sign-In 55102 $restoreMember $memberEmail | Out-Null
    Api 55102 $restoreMember GET $workspaceRoute $null 404 | Out-Null
    $restoredIssue = Api 55102 $restoreOwner GET $issueRoute
    Assert-Drill ($null -eq $restoredIssue.source -and $restoredIssue.triage.nextStep -eq 'Retain this decision') 'Restored source was visible early, or private data was lost.'
    Assert-Drill ((Api 55102 $restoreOwner GET "$issueRoute/activity").total -eq 2) 'Restored issue history differs.'
    Assert-Drill ((Api 55102 $restoreOwner GET "$runsRoute/$($run.runId)").state -eq 'cancelled') 'The restored refresh could still run.'
    $invitations = Api 55102 $restoreOwner GET "$workspaceRoute/invitations"
    Assert-Drill (($invitations.items | Where-Object invitationId -EQ $pending.invitation.invitationId).state -eq 'revoked') 'The restored invitation was usable.'
    $fixturePath = Join-Path $repo 'apps/web/e2e/github-fixture.mjs'
    docker run -d --name $fixture --network "container:$restored" --mount "type=bind,src=$fixturePath,dst=/fixture.mjs,readonly" node:24 node /fixture.mjs | Out-Null
    Native-Ok 'Start isolated public GitHub fixture'
    $containers.Add($fixture)
    Start-Sleep -Seconds 2
    $recheck = Api 55102 $restoreOwner POST $runsRoute @{ runId = [Guid]::NewGuid() } 201
    for ($i = 0; $i -lt 8 -and $recheck.state -ne 'succeeded'; $i++) {
        $recheck = Api 55102 $restoreOwner POST "$runsRoute/$($recheck.runId)/advance" @{ expectedCheckpoint = $recheck.checkpoint }
    }
    Assert-Drill ($recheck.state -eq 'succeeded') 'Public source validation failed.'
    Assert-Drill ((Api 55102 $restoreOwner GET $issueRoute).source.title -eq 'CLI crashes on Windows') 'Verified public content did not reappear.'
    $reopened = [DateTimeOffset]::UtcNow

    docker run --rm --mount "type=bind,src=$PSScriptRoot,dst=/ops,readonly" -e TRIAGEDESK_CHECK_EPOCH=1800000000 postgres:18 bash /ops/check-backup-age.sh 2027-01-15T06:00:00Z 18 | Out-Null
    Native-Ok 'Accept a fresh backup heartbeat'
    docker run --rm --mount "type=bind,src=$PSScriptRoot,dst=/ops,readonly" -e TRIAGEDESK_CHECK_EPOCH=1800000000 postgres:18 bash /ops/check-backup-age.sh 2027-01-13T00:00:00Z 18 2>$null | Out-Null
    Assert-Drill ($LASTEXITCODE -eq 1) 'The missed-backup age guard did not reject a stale snapshot.'

    $report = @(
        'Triage Desk Slice 8 local release and recovery drill'
        "Previous commit $((git rev-parse HEAD).Trim())"
        "Previous image $previous"
        "Candidate local source stamp $stamp"
        "Candidate image $candidate"
        "Schema $earlierMigration upgraded to 20261125155813_DurableProtectionKeys"
        'Candidate readiness returned 204 with the expected release stamp.'
        'The previous image used the runtime role to read the candidate decision and its two history entries on the upgraded schema.'
        'Read-only export, age encryption and decryption, SQL restore, and ciphertext and archive hashes verified.'
        'Old session rejected, invitation revoked, open run cancelled, member removal reconciled, and public source reverified.'
        'Fresh and missed-backup age gates exercised locally.'
        ("Recovery point age {0:F1} seconds ({1:O} snapshot)." -f ($detected - $snapshot).TotalSeconds, $snapshot)
        ("Detection to safe reviewer access {0:F1} seconds." -f ($reopened - $detected).TotalSeconds)
        'Hosted deployment and independent alarm delivery require isolated provider resources.'
    )
    $report | Set-Content -LiteralPath $EvidencePath
    $report
} finally {
    $cleanup = $containers.ToArray()
    [Array]::Reverse($cleanup)
    foreach ($name in $cleanup) { docker rm -f -v $name 2>$null | Out-Null }
    docker network rm $network 2>$null | Out-Null
    Pop-Location
}
