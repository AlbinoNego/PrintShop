# PrintShop — Sistema de Impressão Online

Sistema web completo em **ASP.NET Core 8** para papelarias receberem pedidos de impressão online, com upload de arquivos, configuração de opções, pagamento via PIX ou na loja, e **impressão automática via Windows** após confirmação do pagamento.

---

## 🚀 Requisitos

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows (para impressão automática real)
- Adobe Acrobat Reader (para imprimir PDFs automaticamente)
- Microsoft Office (para Word e PowerPoint)

---

## ⚙️ Instalação e execução

```bash
# 1. Entrar na pasta do projeto
cd PrintShop

# 2. Restaurar dependências
dotnet restore

# 3. Rodar em desenvolvimento
dotnet run
```

O sistema estará disponível em: **http://localhost:5000**

---

## 📁 Estrutura do projeto

```
PrintShop/
├── Controllers/
│   ├── HomeController.cs        # Página inicial
│   └── OrderController.cs       # Toda a lógica de pedidos
├── Models/
│   └── PrintOrder.cs            # Modelo do pedido + enums
├── Services/
│   ├── OrderQueueService.cs     # Fila de pedidos (in-memory)
│   ├── PricingService.cs        # Cálculo de preços
│   ├── PrinterService.cs        # Impressão via Windows
│   └── PixService.cs            # Geração de código PIX EMV
├── Views/
│   ├── Home/Index.cshtml        # Página inicial
│   ├── Order/
│   │   ├── New.cshtml           # Formulário de novo pedido
│   │   ├── Review.cshtml        # Revisão e preço
│   │   ├── Payment.cshtml       # Escolha do pagamento
│   │   ├── PixPayment.cshtml    # QR Code PIX
│   │   ├── Success.cshtml       # Confirmação + status
│   │   └── Queue.cshtml         # Painel da papelaria
│   └── Shared/_Layout.cshtml    # Layout base
└── wwwroot/
    ├── css/site.css             # Estilos
    ├── js/site.js               # Scripts
    └── uploads/                 # Arquivos dos clientes
```

---

## 🖨️ Configurar impressão automática (Windows)

O arquivo `Services/PrinterService.cs` usa `Process.Start` para enviar arquivos para a impressora padrão do Windows.

### Para PDFs:
Certifique-se que o **Adobe Acrobat Reader** está instalado e no PATH, ou ajuste o caminho em `GetPrintExecutable()`:
```csharp
".pdf" => @"C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe",
```

### Para Word/PowerPoint:
O Office precisa estar instalado. O comando `/p` instrui o Office a imprimir e fechar.

### Impressora específica:
Para selecionar uma impressora específica ao invés da padrão, use o serviço de listagem:
```csharp
var printers = printerService.GetAvailablePrinters();
```

---

## 💳 Configurar PIX real

Edite `appsettings.json`:
```json
{
  "PrintShop": {
    "PixKey": "sua-chave-pix@email.com",
    "MerchantName": "Nome da Papelaria",
    "MerchantCity": "SUA CIDADE"
  }
}
```

O código PIX gerado é no **formato EMV padrão BACEN** — funciona em qualquer banco.

Para integração com API bancária (confirmação automática), substitua `PixService.cs` pelo SDK do banco escolhido:
- [Mercado Pago](https://www.mercadopago.com.br/developers)
- [PagSeguro](https://dev.pagseguro.uol.com.br/)
- [Efí Bank](https://dev.efipay.com.br/)

---

## 💰 Tabela de preços

Edite `Services/PricingService.cs` para ajustar os preços:

| Opção | Preço padrão |
|---|---|
| Preto e branco (por página) | R$ 0,50 |
| Colorido (por página) | R$ 1,50 |
| Papel A4 90g | +R$ 0,20/pág |
| Papel A3 | +R$ 0,50/pág |
| Papel fotográfico | +R$ 1,00/pág |
| Plastificação | R$ 3,50/folha |
| Frente e verso | −20% |

---

## 🗄️ Banco de dados (próximo passo)

Atualmente os pedidos ficam **em memória** (se reiniciar o servidor, perde os dados). Para produção, adicione Entity Framework Core:

```bash
dotnet add package Microsoft.EntityFrameworkCore.SqlServer
dotnet add package Microsoft.EntityFrameworkCore.Tools
```

---

## 📱 Fluxo do cliente

1. **Acessa o site** → clica em "Fazer Pedido"
2. **Envia arquivos** (PDF, Word, PPT, JPG, PNG)
3. **Configura a impressão** (cor, cópias, papel, plastificação)
4. **Revisa o pedido** com o valor calculado
5. **Escolhe pagamento** (PIX ou na loja)
6. **PIX**: escaneia QR Code → sistema imprime automaticamente
7. **Na loja**: retira e paga presencialmente

## 🏪 Painel da papelaria

Acesse `/Order/Queue` para ver todos os pedidos, status em tempo real e confirmar retiradas.

---

## 🔒 Segurança para produção

- [ ] Adicionar autenticação no painel `/Order/Queue`
- [ ] Configurar HTTPS
- [ ] Limitar tamanho de upload no servidor (já limitado a 50MB no código)
- [ ] Usar banco de dados persistente
- [ ] Integrar webhook do banco para confirmar PIX automaticamente
- [ ] Adicionar rate limiting para uploads
