function Write-UpdateJson {
    param([string]$Path, $Value)
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 8 -Compress), [Text.UTF8Encoding]::new($false))
}

function Get-UpdateSigning {
    param([string[]]$Signers, [string[]]$AdditionalKeys, [string]$Pfx, [securestring]$PfxPassword, [string]$Workspace)

    $certificates = [System.Collections.Generic.List[System.Security.Cryptography.X509Certificates.X509Certificate2]]::new()
    $keys = @()
    $publicKeys = [ordered]@{}
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('My', 'CurrentUser')
    try {
        $signerCertificates = @()
        if ($Pfx) {
            $path = (Resolve-Path -LiteralPath $Pfx).Path
            if ($path.StartsWith($Workspace.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'A test signing key must be outside the workspace.'
            }
            $password = if ($PfxPassword) { [pscredential]::new('test', $PfxPassword).GetNetworkCredential().Password } else { $null }
            $cert = [System.Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12FromFile(
                $path, $password, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
            $signerCertificates += $cert
            $certificates.Add($cert)
        }
        if ($Signers -or $AdditionalKeys) {
            $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
            foreach ($thumbprint in (@($Signers) + @($AdditionalKeys) | Where-Object { $_ } | Select-Object -Unique)) {
                $found = $store.Certificates.Find('FindByThumbprint', $thumbprint, $false)
                if ($found.Count -ne 1) { throw 'The update certificate was not found in CurrentUser/My.' }
                $certificates.Add($found[0])
                if ($thumbprint -in $Signers) { $signerCertificates += $found[0] }
            }
        }
        if (!$signerCertificates) {
            throw 'Specify the signing certificate in CurrentUser/My using -SigningCertificate.'
        }
        foreach ($cert in $certificates) {
            $rsaPublic = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($cert)
            if (!$rsaPublic) { throw 'An RSA certificate is required.' }
            try {
                if ($rsaPublic.KeySize -lt 2048) { throw 'An RSA public key of at least 2048 bits is required.' }
                $publicKey = $rsaPublic.ExportSubjectPublicKeyInfo()
                $keyId = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($publicKey)).ToLowerInvariant()
                $publicKeys[$keyId] = [Convert]::ToBase64String($publicKey)
            }
            finally { $rsaPublic.Dispose() }
            if ($cert -in $signerCertificates) {
                $rsaPrivate = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
                if (!$rsaPrivate) { throw 'The signing certificate does not have an accessible private key.' }
                $keys += [pscustomobject]@{ Id = $keyId; Rsa = $rsaPrivate }
            }
        }
        if ($keys.Count -gt 8) { throw 'Too many signing certificates.' }
        return @{ Keys = $keys; PublicKeys = $publicKeys; Certificates = $certificates }
    }
    catch {
        $keys | ForEach-Object { $_.Rsa.Dispose() }
        $certificates | ForEach-Object { $_.Dispose() }
        throw
    }
    finally { $store.Dispose() }
}

function Write-SignedUpdate {
    param([string]$Destination, [string]$Zip, [string]$Version, [string]$ProfilePath, $Signing, [string]$VerificationAssembly)

    $configuration = Get-Content -LiteralPath $ProfilePath -Raw | ConvertFrom-Json
    $manifestPath = Join-Path $Destination 'update-manifest.json'
    $signaturesPath = Join-Path $Destination 'update-signatures.json'
    Write-UpdateJson $manifestPath ([ordered]@{
        product = 'HandpickerStudio'
        repository = $configuration.repository
        version = $Version
        runtime = 'win-x64'
        asset = (Get-Item -LiteralPath $Zip).Name
        size = (Get-Item -LiteralPath $Zip).Length
        sha256 = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
    })
    [byte[]]$manifest = [IO.File]::ReadAllBytes($manifestPath)
    $proofs = @($Signing.Keys | ForEach-Object {
        $signature = $_.Rsa.SignData($manifest, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pss)
        [ordered]@{ key = $_.Id; signature = [Convert]::ToBase64String($signature) }
    })
    Write-UpdateJson $signaturesPath $proofs

    $context = [Runtime.Loader.AssemblyLoadContext]::new('HandpickerStudio release validation', $true)
    try {
        $input = [IO.MemoryStream]::new([IO.File]::ReadAllBytes($VerificationAssembly))
        try { $assembly = $context.LoadFromStream($input) }
        finally { $input.Dispose() }
        $packageType = $assembly.GetType('HandpickerStudio.Update.UpdatePackage', $true)
        $options = $packageType.GetProperty('Json').GetValue($null)
        $profile = [Text.Json.JsonSerializer]::Deserialize([IO.File]::ReadAllText($ProfilePath),
            $assembly.GetType('HandpickerStudio.Update.UpdateProfile', $true), $options)
        $check = $packageType.GetMethod('ReadManifest').Invoke($null, @(
            $manifest, [IO.File]::ReadAllBytes($signaturesPath), $profile, $Version
        ))
        $stream = [IO.File]::OpenRead($Zip)
        try { $packageType.GetMethod('VerifyZip').Invoke($null, @($stream, $check)) }
        finally { $stream.Dispose() }
    }
    finally { $context.Unload() }
}
