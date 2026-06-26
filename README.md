# PrintShop

O **PrintShop** é um sistema web feito em **ASP.NET Core 8** para facilitar o atendimento de papelarias que recebem muitos pedidos de impressão.

A ideia do projeto é permitir que o cliente faça o pedido pelo próprio site, envie os arquivos, escolha as opções de impressão, veja o valor final, pague via PIX ou combine o pagamento na loja, e acompanhe o andamento do pedido.

O sistema também possui uma área administrativa para a papelaria acompanhar pedidos, autorizar pagamentos, configurar preços, configurar impressoras e consultar relatórios.

---

## Funcionalidades

- Envio de arquivos PDF, Word, PowerPoint, JPG e PNG.
- Contagem automática de páginas dos arquivos enviados.
- Cálculo do valor com base em páginas, cópias, papel, plastificação e entrega.
- Escolha entre retirada na loja ou entrega.
- Pagamento via PIX ou pagamento presencial para retirada.
- Acompanhamento do pedido pelo código.
- Edição do pedido antes da confirmação do pagamento.
- Armazenamento dos arquivos fora da pasta pública do site.
- Banco de dados SQLite para salvar pedidos e configurações.
- Painel administrativo com login.
- Filtro e busca de pedidos no painel administrativo.
- Relatórios administrativos.
- Configuração de preços pelo admin.
- Configuração de impressoras pelo admin.
- Reimpressão de pedidos.
- Webhook PIX para confirmação automática de pagamento.
- HTTPS configurado.
- Rate limiting para uploads, webhook e requisições gerais.

---

## Tecnologias utilizadas

- ASP.NET Core 8 MVC
- Razor Views
- SQLite
- HTML, CSS e JavaScript
- Data Protection do ASP.NET Core
- Rate Limiting nativo do ASP.NET Core

---

## Como executar o projeto

Primeiro, clone o repositório:

```powershell
git clone <url-do-repositorio>
```

Depois, entre na pasta do projeto:

```powershell
cd PrintShop-main
```

Restaure as dependências:

```powershell
dotnet restore
```

Execute o projeto:

```powershell
dotnet run
```

O projeto foi configurado para rodar com HTTPS no ambiente local.

Caso o navegador mostre aviso de certificado, execute:

```powershell
dotnet dev-certs https --trust
```

No Visual Studio, o ideal é abrir o projeto pelo arquivo `.csproj` ou por uma solução `.sln`. Abrir somente a pasta pode fazer o Visual Studio usar outra configuração de inicialização.

---

## Configurações

As configurações principais ficam no arquivo `appsettings.json`.

Exemplo:

```json
{
  "PrintShop": {
    "PixKey": "sua-chave-pix",
    "MerchantName": "NOME DA LOJA",
    "MerchantCity": "CIDADE",
    "PixWebhookSecret": "configure-um-segredo-forte",
    "MaxFileSizeMB": 50,
    "DefaultPrinter": "",
    "Admin": {
      "Username": "configure-um-usuario",
      "Password": "configure-uma-senha"
    }
  }
}
```

Para um ambiente real, o ideal é não deixar senhas e segredos diretamente no `appsettings.json`. O recomendado é usar variáveis de ambiente ou outro método mais seguro de configuração.

---

## Estrutura do projeto

```text
PrintShop-main/
├── Controllers/
├── Models/
├── Services/
├── Views/
├── wwwroot/
└── App_Data/
```

A pasta `App_Data` é usada para dados internos da aplicação:

```text
App_Data/
├── printshop.db
├── uploads/
└── keys/
```

Os arquivos enviados pelos clientes ficam em `App_Data/uploads`, fora do `wwwroot`, para evitar acesso público direto pelo navegador.

---

## Fluxo do cliente

1. O cliente acessa o site.
2. Envia os arquivos que deseja imprimir.
3. Escolhe as opções de impressão.
4. Escolhe retirada ou entrega.
5. Informa os dados de contato.
6. Revisa o pedido e o valor final.
7. Escolhe a forma de pagamento.
8. Recebe o código do pedido.
9. Acompanha o andamento pelo site.

Depois que o pedido é confirmado, ele não pode mais ser editado pelo cliente.

---

## Área administrativa

As principais rotas administrativas são:

```text
/Admin/Login
/Order/Queue
/Admin/Reports
/Admin/Settings
```

No painel administrativo é possível:

- visualizar os pedidos;
- filtrar por status;
- buscar por código, cliente, telefone ou arquivo;
- ver detalhes do pedido;
- autorizar pagamento;
- barrar pedido;
- marcar pedido como pronto;
- marcar pedido como entregue;
- reimprimir pedido;
- configurar impressoras;
- pausar a impressão automática;
- configurar preços;
- consultar relatórios.

---

## Preços

Os preços são configuráveis pela tela administrativa.

Atualmente o sistema permite configurar:

- preço por página em preto e branco;
- preço por página colorida;
- adicional por tipo de papel;
- valor da plastificação;
- taxa de entrega.

O sistema não aplica desconto automático para frente e verso.

---

## Impressão automática

A parte de impressão fica no serviço:

```text
Services/PrinterService.cs
```

O comportamento atual é:

- PDF: impressão usando leitor PDF instalado ou fallback do Windows;
- Word: impressão usando a ação de impressão associada do Windows;
- PowerPoint: impressão usando PowerPoint quando disponível;
- imagens: impressão pelo aplicativo associado;
- fora do Windows: simulação de impressão.

As impressoras podem ser configuradas pela tela `/Admin/Settings`.

Nos testes locais, a impressão funcionou com PDF, PPT/PPTX, DOC/DOCX e imagens.

---

## PIX e webhook

O sistema possui um endpoint de webhook para confirmação automática de pagamento PIX:

```http
POST /Pix/Webhook
```

Esse webhook exige um segredo configurado na aplicação.

A implementação atual é genérica e deve ser adaptada ao banco ou gateway de pagamento que for usado em produção.

---

## Segurança

Algumas medidas já foram adicionadas:

- HTTPS;
- HSTS fora do ambiente de desenvolvimento;
- cookies de sessão seguros;
- rate limiting para uploads;
- rate limiting para webhook PIX;
- arquivos enviados fora da pasta pública;
- painel administrativo protegido por login.

Mesmo assim, antes de usar em produção, ainda é necessário revisar pontos importantes de segurança.

---

## Pontos que ainda precisam ser melhorados

- Integrar um provedor PIX real.
- Configurar domínio e certificado HTTPS real.
- Trocar credenciais e segredos por configuração segura.
- Melhorar o login administrativo.
- Criar rotina de backup do banco.
- Criar limpeza automática de arquivos antigos.
- Validar melhor os tipos reais dos arquivos enviados.
- Adicionar histórico de ações administrativas.
- Testar a impressão no ambiente físico da papelaria.

---

## Status do projeto

O projeto ainda está em desenvolvimento.

A versão atual já possui o fluxo principal funcionando, mas ainda precisa de ajustes e validações antes de ser usada em produção.
