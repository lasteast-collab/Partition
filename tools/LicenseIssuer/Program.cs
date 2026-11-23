using System.Security.Cryptography;
using Lasteast.Core.Licensing;

// lasteast-license new-key <private.pem>
//     Creates a signing key. Prints the public key to put into LicenseKeys.Trusted. Keep private.pem secret and OUT of the repository.
// lasteast-license issue <private.pem> <Free|Pro|Technician> "<Licensee>" [--expires yyyy-mm-dd] [--out license.json]

if (args.Length >= 2 && args[0] == "new-key")
{
    if (File.Exists(args[1])) { Console.Error.WriteLine("Refusing to overwrite " + args[1]); return 1; }
    using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(args[1], k.ExportPkcs8PrivateKeyPem());
    Console.WriteLine("Public key (add to LicenseKeys.Trusted):");
    Console.WriteLine(Convert.ToBase64String(k.ExportSubjectPublicKeyInfo()));
    return 0;
}
if (args.Length >= 4 && args[0] == "issue")
{
    using var k = ECDsa.Create();
    k.ImportFromPem(File.ReadAllText(args[1]));
    var edition = Enum.Parse<Edition>(args[2], ignoreCase: true);
    int i = Array.IndexOf(args, "--expires");
    DateTimeOffset? expires = i > 0 ? DateTimeOffset.Parse(args[i + 1] + "T23:59:59Z") : null;
    int o = Array.IndexOf(args, "--out");
    var license = new License(edition, args[3], Guid.NewGuid().ToString("N")[..16].ToUpperInvariant(), DateTimeOffset.UtcNow, expires);
    var json = Licenses.Sign(license, k);
    File.WriteAllText(o > 0 ? args[o + 1] : "license.json", json);
    Console.WriteLine(json);
    return 0;
}
Console.WriteLine("usage: lasteast-license new-key <private.pem> | issue <private.pem> <Free|Pro|Technician> \"<Licensee>\" [--expires yyyy-mm-dd] [--out file]");
return 2;
