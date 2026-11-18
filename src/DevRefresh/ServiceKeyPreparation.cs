using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevRefresh;

public static class ServiceKeyPreparation
{
    public const string Marker=".service-keys-machine";
    public static object Prepare(string dataPath)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Подготовка службы доступна только в Windows.");
        var directory=Path.Combine(dataPath,"keys");
        Directory.CreateDirectory(directory);
        if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("Каталог ключей является ссылкой.");
        var marker=Path.Combine(dataPath,Marker);
        if(File.Exists(marker)){ValidateSecret(dataPath,directory);return new{Prepared=true,KeysRewrapped=0};}
        var prepared=new List<(string Path,string Original,string Updated)>();
        var decryptor=new DpapiXmlDecryptor(null);
        var encryptor=new DpapiXmlEncryptor(true,NullLoggerFactory.Instance);
        try
        {
            foreach(var file in Directory.EnumerateFiles(directory,"*.xml"))
            {
                if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new IOException("Файл ключа является ссылкой.");
                var original=File.ReadAllText(file);var document=XDocument.Parse(original);
                foreach(var secret in document.Descendants().Where(e=>e.Name.LocalName=="encryptedSecret").ToArray())
                {
                    if(((string?)secret.Attribute("decryptorType"))?.Split(',')[0]!=typeof(DpapiXmlDecryptor).FullName)
                        throw new InvalidOperationException("Метод шифрования существующих ключей не поддерживает автоматический переход на службу.");
                    var plain=decryptor.Decrypt(secret.Elements().Single());
                    var encrypted=encryptor.Encrypt(plain);
                    secret.SetAttributeValue("decryptorType",encrypted.DecryptorType.AssemblyQualifiedName);
                    secret.ReplaceNodes(encrypted.EncryptedElement);
                }
                // An unencrypted legacy master key is wrapped as well.
                foreach(var plain in document.Descendants().Where(e=>e.Attributes().Any(a=>a.Name.LocalName=="requiresEncryption"&&a.Value=="true")).ToArray())
                {
                    var encrypted=encryptor.Encrypt(plain);
                    plain.ReplaceWith(new XElement(XName.Get("encryptedSecret","http://schemas.asp.net/2015/03/dataProtection"),
                        new XAttribute("decryptorType",encrypted.DecryptorType.AssemblyQualifiedName!),encrypted.EncryptedElement));
                }
                prepared.Add((file,original,document.ToString(SaveOptions.DisableFormatting)));
            }
        }
        catch(Exception ex)
        {throw new InvalidOperationException("Не удалось подготовить ключи. Запустите STARTSERVICE от имени Windows-пользователя, который ранее запускал приложение, либо восстановите доступ к его ключам. Служба не создана.",ex);}
        try
        {
            foreach(var file in prepared)AtomicWrite(file.Path,file.Updated);
            ValidateSecret(dataPath,directory);
            AtomicWrite(marker,"DPAPI LocalMachine; protected by service data directory ACL.");
        }
        catch
        {
            foreach(var file in prepared)AtomicWrite(file.Path,file.Original);
            throw;
        }
        return new{Prepared=true,KeysRewrapped=prepared.Count};
    }
    private static void AtomicWrite(string path,string value)
    {
        var temporary=path+".service-"+Guid.NewGuid().ToString("N");
        try{File.WriteAllText(temporary,value);File.Move(temporary,path,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private static void ValidateSecret(string dataPath,string keyPath)
    {
        var database=Path.Combine(dataPath,"devrefresh.sqlite");
        if(!File.Exists(database))return;
        using var db=DatabaseMigrator.Connect(database,SqliteOpenMode.ReadOnly);
        using var cmd=db.CreateCommand();cmd.CommandText="SELECT value FROM settings WHERE id=1";
        if(cmd.ExecuteScalar() is not string json)return;
        var settings=JsonSerializer.Deserialize<SqlSettings>(json);
        if(settings is null)return;
        var provider=DataProtectionProvider.Create(new DirectoryInfo(keyPath),options=>options.SetApplicationName("DevRefresh"));
        if(!string.IsNullOrEmpty(settings.ProtectedPassword))
            _=provider.CreateProtector("DevRefresh.SqlPassword.v1").Unprotect(settings.ProtectedPassword);
        if(settings.DtRestore is {} dt)
            foreach(var password in new[]{dt.ProtectedSqlPassword}.Where(x=>x.Length>0))
                _=provider.CreateProtector(DtRestoreConfiguration.PasswordPurpose).Unprotect(password);
    }
}
