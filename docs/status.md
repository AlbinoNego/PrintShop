# Status do Projeto - PrintShop

Última atualização: versão estável local após envio para GitHub.

## Objetivo

O PrintShop é um sistema ASP.NET Core 8 MVC para papelarias receberem pedidos de impressão online.

O cliente envia arquivos, configura impressão, escolhe retirada ou entrega, revisa o valor, paga via PIX ou na loja, e acompanha o pedido pelo código.

O admin acompanha pedidos, confirma pagamentos, gerencia impressão, configura preços/impressoras e consulta relatórios.

## Estado atual

Funcionalidades implementadas:

- Upload de PDF, Word, PowerPoint, JPG e PNG.
- Arquivos salvos fora do `wwwroot`, em `App_Data/uploads`.
- Pedidos e configurações salvos em SQLite, em `App_Data/printshop.db`.
- Contagem real de páginas dos arquivos.
- Cálculo de preço por página, cópias por arquivo, tipo de papel, plastificação e entrega.
- Orientação retrato/paisagem.
- Pré-visualização disponível para PDF.
- Edição de pedido antes do pagamento sem perder arquivos enviados.
- Remoção de arquivos durante edição do pedido.
- Busca de pedido pelo código.
- Telefone obrigatório para criar pedido.
- Retirada ou entrega.
- Entrega vai direto para PIX.
- Retirada permite PIX ou pagamento na loja.
- Pedido fica como rascunho durante a revisão e só entra no painel admin após confirmação da forma de pagamento.
- Painel admin com login.
- Admin pode autorizar pagamento, barrar pedido, marcar como pronto, marcar como entregue e reimprimir.
- Painel admin tem busca, filtro por status e detalhes do pedido.
- Relatórios administrativos.
- Tela admin `/Admin/Settings` para preços, impressoras e pausa da impressão automática.
- Impressão automática funcionando em testes para PDF, PPT/PPTX, DOC/DOCX e imagens.
- Word/PowerPoint imprimem direto quando a orientação não muda; quando muda, o sistema gera PDF temporário para aplicar a orientação.
- HTTPS configurado.
- Cookies de sessão seguros.
- HSTS fora de desenvolvimento.
- Rate limiting global, para upload e para webhook PIX.
- Webhook PIX genérico em `/Pix/Webhook`.

## GitHub

Repositório:

```text
https://github.com/AlbinoNego/PrintShop
```

Branch principal:

```text
main
```

O repositório já recebeu push da versão atual.

## Rotas principais

Cliente:

```text
/
/Order/New
/Order/Review/{id}
/Order/Payment/{id}
/Order/PixPayment/{id}
/Order/Success/{id}
/Order/MyOrders
```

Admin:

```text
/Admin/Login
/Order/Queue
/Admin/Reports
/Admin/Settings
```

Webhook:

```text
POST /Pix/Webhook
```

## Arquivos importantes

Controllers:

```text
Controllers/OrderController.cs
Controllers/AdminController.cs
Controllers/PixController.cs
```

Services:

```text
Services/OrderQueueService.cs
Services/FileStorageService.cs
Services/PageCountService.cs
Services/PricingService.cs
Services/PrinterService.cs
Services/AdminSettingsService.cs
Services/PixService.cs
```

Models:

```text
Models/PrintOrder.cs
Models/AdminSettings.cs
Models/AdminReportViewModel.cs
```

Views principais:

```text
Views/Order/New.cshtml
Views/Order/Review.cshtml
Views/Order/Payment.cshtml
Views/Order/PixPayment.cshtml
Views/Order/Success.cshtml
Views/Order/Queue.cshtml
Views/Admin/Settings.cshtml
Views/Admin/Reports.cshtml
```

## Decisões importantes

- Uploads não ficam em `wwwroot`.
- SQLite é usado como banco local.
- Cliente não tem login; acompanha pedido por código.
- Admin tem login simples por configuração.
- Pedido só pode ser editado antes da confirmação do pagamento.
- Entrega obriga pagamento via PIX.
- Retirada pode usar PIX ou pagamento presencial.
- Webhook PIX é genérico e precisa ser adaptado ao provedor real.
- Impressão usa aplicativos instalados no Windows e ações de impressão do sistema.
- PDFs enviados pelo cliente não são alterados pelo fluxo de orientação.
- Configuração de impressoras fica somente na área admin.
- Preços são editáveis no admin e persistidos em SQLite.

## Configurações sensíveis

O `appsettings.json` foi deixado com valores genéricos para GitHub.

Antes de produção, configurar:

```text
PrintShop:PixKey
PrintShop:MerchantName
PrintShop:MerchantCity
PrintShop:PixWebhookSecret
PrintShop:Admin:Username
PrintShop:Admin:Password
```

Recomendado usar variáveis de ambiente ou outro provedor seguro de configuração em produção.

## Rate limiting atual

Configurado em `Program.cs`.

- Global: 120 requisições por minuto por IP.
- Upload: 20 envios a cada 10 minutos.
- Webhook PIX: 60 chamadas por minuto.

## HTTPS

Configurado em `Program.cs`.

Inclui:

- redirecionamento HTTPS;
- HSTS fora de desenvolvimento;
- cookies seguros;
- suporte a `X-Forwarded-For` e `X-Forwarded-Proto`.

Perfil local usa HTTPS em `Properties/launchSettings.json`.

## Impressão

Testado localmente:

- PDF: funcionando.
- PPT/PPTX: funcionando.
- DOC/DOCX: funcionando.
- Imagens: funcionando.

Observação:

- A configuração de impressoras é feita em `/Admin/Settings`.
- Quando uma impressora específica é definida, o sistema troca temporariamente a impressora padrão do Windows para imprimir e depois restaura a anterior.
- Para testes com VM local, a VM precisa enxergar as impressoras instaladas na rede/localmente.

## Pendências recomendadas

Prioridades futuras:

1. Integração real com provedor PIX.
2. Login admin mais robusto, com senha com hash e usuários no banco.
3. Auditoria de ações administrativas.
4. Backup do SQLite.
5. Limpeza automática de arquivos antigos.
6. Validação mais forte do tipo real dos arquivos.
7. Configuração real de domínio e certificado HTTPS em produção.
8. Melhorias de entrega: bairros, taxa por bairro, saiu para entrega, entregue.
9. Relatórios por período e exportação CSV/Excel.

## Comandos úteis

Build:

```powershell
dotnet build --no-restore /p:UseAppHost=false
```

Executar:

```powershell
dotnet run
```

Confiar no certificado local:

```powershell
dotnet dev-certs https --trust
```

Git:

```powershell
git status
git add .
git commit -m "mensagem"
git push
```
