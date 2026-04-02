namespace PrintShop.Services;

/// <summary>
/// Serviço de geração de PIX estático (copia e cola).
/// Para produção, integrar com API de banco (ex: Mercado Pago, PagSeguro, Efí Bank).
/// </summary>
public class PixService
{
    // Configure estes dados com os dados reais da papelaria
    private const string PixKey = "papelaria@email.com"; // Chave PIX
    private const string MerchantName = "Papelaria Print";
    private const string MerchantCity = "PORTO VELHO";

    public string GeneratePixCode(decimal amount, string orderId)
    {
        // Formato EMV (padrão BACEN) simplificado para PIX estático
        string description = $"Pedido #{orderId}";
        string amountStr = amount.ToString("F2").Replace(",", ".");

        var pix = new PixEmvBuilder()
            .SetPixKey(PixKey)
            .SetMerchantName(MerchantName)
            .SetMerchantCity(MerchantCity)
            .SetAmount(amountStr)
            .SetDescription(description)
            .SetTxId(orderId)
            .Build();

        return pix;
    }
}

/// <summary>
/// Builder do código EMV para QR Code PIX (padrão BACEN).
/// </summary>
public class PixEmvBuilder
{
    private string _key = "";
    private string _name = "";
    private string _city = "";
    private string _amount = "";
    private string _description = "";
    private string _txId = "";

    public PixEmvBuilder SetPixKey(string key) { _key = key; return this; }
    public PixEmvBuilder SetMerchantName(string name) { _name = name[..Math.Min(name.Length, 25)]; return this; }
    public PixEmvBuilder SetMerchantCity(string city) { _city = city[..Math.Min(city.Length, 15)]; return this; }
    public PixEmvBuilder SetAmount(string amount) { _amount = amount; return this; }
    public PixEmvBuilder SetDescription(string desc) { _description = desc[..Math.Min(desc.Length, 25)]; return this; }
    public PixEmvBuilder SetTxId(string txId) { _txId = txId[..Math.Min(txId.Length, 25)]; return this; }

    public string Build()
    {
        // GUI + chave PIX
        string gui = Field("00", "BR.GOV.BCB.PIX") + Field("01", _key);
        if (!string.IsNullOrEmpty(_description))
            gui += Field("02", _description);

        string merchantAccountInfo = Field("26", gui);
        string merchantName = Field("59", _name);
        string merchantCity = Field("60", _city);
        string txId = Field("62", Field("05", _txId));
        string amount = string.IsNullOrEmpty(_amount) ? "" : Field("54", _amount);

        string payload =
            Field("00", "01") +           // Payload Format Indicator
            merchantAccountInfo +          // Merchant Account Information
            Field("52", "0000") +         // Merchant Category Code
            Field("53", "986") +          // Transaction Currency (BRL)
            amount +                       // Transaction Amount
            Field("58", "BR") +           // Country Code
            merchantName +                 // Merchant Name
            merchantCity +                 // Merchant City
            txId;                          // Additional Data Field

        // CRC16 CCITT
        string crcInput = payload + "6304";
        string crc = CalculateCrc16(crcInput);

        return payload + Field("63", crc);
    }

    private static string Field(string id, string value)
    {
        string len = value.Length.ToString("D2");
        return $"{id}{len}{value}";
    }

    private static string CalculateCrc16(string input)
    {
        const ushort polynomial = 0x1021;
        ushort crc = 0xFFFF;
        foreach (char c in input)
        {
            crc ^= (ushort)(c << 8);
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ polynomial)
                    : (ushort)(crc << 1);
        }
        return crc.ToString("X4");
    }
}
