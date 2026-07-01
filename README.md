# PrintShop

PrintShop é um sistema web em ASP.NET Core 8 criado para automatizar pedidos de impressão em papelarias.

A ideia é diminuir o atendimento manual por WhatsApp: o cliente acessa o site, envia os arquivos, escolhe as opções de impressão, revisa o valor, escolhe a forma de pagamento e acompanha o andamento do pedido pelo código.

O sistema também possui uma área administrativa para a papelaria acompanhar pedidos, autorizar pagamentos, configurar preços, configurar impressoras e consultar relatórios.

Este projeto ainda está em desenvolvimento e não deve ser usado em produção sem revisão de segurança, backup, credenciais reais e integração definitiva de pagamento.

---

## Funcionalidades

- Envio de arquivos PDF, Word, PowerPoint, JPG e PNG.
- Arquivos armazenados fora da pasta pública do site.
- Banco SQLite para pedidos e configurações.
- Contagem real de páginas dos arquivos.
- Cálculo por página, tipo de papel, plastificação, entrega e cópias por arquivo.
- Escolha entre preto e branco ou colorido.
- Escolha entre frente simples ou frente e verso.
- Escolha entre retrato e paisagem.
- Edição do pedido antes da confirmação da forma de pagamento.
- Pré-visualização disponível para PDF.
- Escolha entre retirada na loja ou entrega.
- Entrega direcionada para PIX.
- Retirada com PIX ou pagamento na loja.
- Pedido só entra no painel administrativo depois que o cliente confirma a forma de pagamento.
- Busca de pedido pelo código.
- Painel administrativo com login.
- Filtros, busca e detalhes de pedidos no admin.
- Autorização manual de pagamento pelo admin.
- Reimpressão, cancelamento, marcação como pronto e entregue.
- Relatórios administrativos.
- Configuração de preços e impressoras pelo admin.
- Webhook PIX genérico.
- HTTPS, HSTS fora de desenvolvimento e cookies seguros.
- Rate limiting global, para upload e para webhook.

---

## Tecnologias

- ASP.NET Core 8 MVC
- Razor Views
- SQLite
- HTML, CSS e JavaScript
- IIS/Windows para hospedagem recomendada
- Data Protection do ASP.NET Core
- Rate Limiting nativo do ASP.NET Core

---

## Como executar localmente

Clone o repositório:

```powershell
git clone https://github.com/AlbinoNego/PrintShop.git
```

Entre na pasta:

```powershell
cd PrintShop
```

Restaure e compile:

```powershell
dotnet restore
dotnet build
```

Execute:

```powershell
dotnet run
```

Se o navegador reclamar do certificado HTTPS local:

```powershell
dotnet dev-certs https --trust
```

No Visual Studio, abra o projeto pelo arquivo `.csproj`. Abrir somente a pasta pode fazer o Visual Studio usar outro perfil de inicialização.

---

## Configuração

As configurações ficam em `appsettings.json`.

O arquivo versionado usa valores genéricos:

```json
{
  "PrintShop": {
    "PixKey": "configure-sua-chave-pix",
    "MerchantName": "NOME DA LOJA",
    "MerchantCity": "CIDADE",
    "PixWebhookSecret": "configure-um-segredo-forte",
    "MaxFileSizeMB": 50,
    "PrintExecutables": {
      "Pdf": "",
      "Word": "",
      "PowerPoint": "",
      "Image": ""
    },
    "Admin": {
      "Username": "configure-um-usuario",
      "Password": "configure-uma-senha"
    }
  }
}
```

Para ambiente real, não deixe chave PIX, senha de admin ou segredo de webhook diretamente no repositório. Use variáveis de ambiente, `appsettings.Local.json` fora do Git ou outro provedor seguro.

---

## Armazenamento

Os dados gerados em execução ficam em `App_Data`:

```text
App_Data/
├── printshop.db
├── uploads/
└── keys/
```

Esses arquivos não devem ir para o GitHub.

O `.gitignore` ignora banco SQLite, arquivos enviados, PDFs gerados, chaves locais, logs, build e arquivos de ambiente.

---

## Fluxo do cliente

1. O cliente cria um novo pedido.
2. Envia os arquivos.
3. Escolhe papel, cor, lados, orientação, plastificação, cópias por arquivo e retirada/entrega.
4. Informa nome e telefone.
5. Revisa o pedido.
6. Se voltar para editar, os arquivos já enviados continuam no pedido.
7. Confirma a forma de pagamento.
8. O pedido entra no painel administrativo.
9. O cliente acompanha pelo código.

Enquanto o cliente está apenas revisando, o pedido fica como rascunho e não aparece no painel administrativo.

---

## Impressão

A impressão automática fica em:

```text
Services/PrinterService.cs
```

O comportamento atual é:

- PDF: usa o leitor PDF instalado ou o fallback do Windows.
- Word: imprime diretamente quando a orientação escolhida já é a mesma do arquivo.
- Word: quando precisa mudar orientação, gera um PDF temporário com a nova orientação e imprime esse temporário.
- PowerPoint: segue a mesma lógica do Word.
- Imagens: usa o aplicativo associado no Windows.
- Fora do Windows: simulação de impressão.

Os PDFs enviados pelo cliente não são convertidos nem alterados pelo fluxo de orientação.

As impressoras e preços são configurados em:

```text
/Admin/Settings
```

---

## Área administrativa

Rotas principais:

```text
/Admin/Login
/Order/Queue
/Admin/Reports
/Admin/Settings
```

No admin é possível:

- ver pedidos;
- filtrar por status;
- buscar por código, cliente, telefone ou arquivo;
- ver detalhes;
- autorizar pagamento;
- barrar pedido;
- marcar como pronto;
- marcar como entregue;
- reimprimir;
- configurar preços;
- configurar impressoras;
- pausar impressão automática;
- consultar relatórios.

---

## PIX

O endpoint de webhook é:

```http
POST /Pix/Webhook
```

A implementação atual é genérica. Para produção, precisa adaptar ao banco ou gateway usado, validar assinatura/evento e registrar logs de confirmação.

---

## Segurança

Já existe:

- HTTPS;
- HSTS fora de desenvolvimento;
- cookies seguros;
- rate limiting;
- upload fora do `wwwroot`;
- painel admin com login;
- `.gitignore` para dados locais e arquivos de clientes.

Ainda precisa melhorar antes de produção:

- senha admin com hash e usuários no banco;
- troca de credenciais por variáveis de ambiente;
- backup automático do SQLite;
- limpeza automática de uploads antigos;
- validação mais forte do tipo real dos arquivos;
- auditoria de ações administrativas;
- integração PIX real;
- configuração definitiva de domínio e certificado.

---

## Hospedagem recomendada

Para testes locais com impressão automática, o caminho mais simples é Windows Server em VM local ou servidor local na rede da papelaria.

Como a impressão acontece na máquina onde a aplicação roda, a VM precisa enxergar as impressoras instaladas. Em nuvem, seria necessário VPN, print server ou um agente local de impressão.

Para Windows Server:

- IIS;
- ASP.NET Core Hosting Bundle do .NET 8;
- drivers das impressoras;
- Office instalado, se for imprimir Word/PowerPoint;
- leitor PDF instalado;
- permissões de escrita em `App_Data`.

---

## Comandos úteis

Build:

```powershell
dotnet build --no-restore /p:UseAppHost=false
```

Publicação:

```powershell
dotnet publish -c Release -o C:\Sites\PrintShop
```

Git:

```powershell
git status
git add .
git commit -m "mensagem"
git push origin main
```

---

## Status

A versão atual cobre o fluxo principal de pedido, pagamento, acompanhamento, painel administrativo, relatórios e impressão automática local em Windows.

O projeto ainda precisa de validação em ambiente real de papelaria antes de qualquer uso em produção.
