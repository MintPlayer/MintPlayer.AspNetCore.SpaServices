for ef core migrations use this from project root

`dotnet ef migrations add --startup-project Demo/Prerendering/Demo.Web/Demo.Web.csproj  "initial_setup" --project Demo/Prerendering/Demo.Data/Demo.Data.csproj `

and this to update database

` dotnet ef database update --startup-project Demo/Prerendering/Demo.Web/Demo.Web.csproj --project Demo/Prerendering/Demo.Data/Demo.Data.csproj`

this gonna create RoutingDemo.db SQLite file
