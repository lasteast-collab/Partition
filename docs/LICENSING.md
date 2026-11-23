# Licensing

Lasteast Partition has three editions: **Free**, **Pro** and **Technician**.

## What every edition includes — permanently

All safety, repair, recovery, backup and health features are in every edition, including Free:
partition operations with preview/backup/verification, conversion, move, clone, OS migration, partition and file
recovery, boot recovery and rescue media, account-recovery guidance, data backup, disk health, benchmark, surface
test, monitoring, virtual disks, the assistant, technician reports and the command line. **A license is never
required to protect, repair or recover data.**

## What a license adds

| Edition | Adds |
|---|---|
| Free | Personal use. |
| Pro | Confirms commercial use within one organization. No feature difference. |
| Technician | Confirms use on customers' computers; technician and customer names on reports. |

## License files

A license is a JSON file: `{"license":{edition, licensee, id, issued, expires}, "signature": "…"}`.
The signature is ECDSA P-256 / SHA-256 over a canonical form of the `license` object (see `Licenses.Canonical`).
The app holds only public keys (`LicenseKeys.Trusted`) and verifies offline; nothing is sent anywhere.
Install a license in **Settings › License › Load license file**; it is copied to the data folder.

## Keys

`LicenseKeys.Development` is a **development key**: its private half is in the test suite, so licenses signed with
it prove nothing. Official builds must replace it:

```
dotnet run --project tools/LicenseIssuer -- new-key C:\secure\lasteast-license.pem
#   → prints the public key; put it into LicenseKeys.Trusted (and remove Development for releases)
dotnet run --project tools/LicenseIssuer -- issue C:\secure\lasteast-license.pem Technician "Contoso Repair" --expires 2027-12-31 --out contoso.json
```

Keep the private key outside the repository and outside the build machine's workspace.
