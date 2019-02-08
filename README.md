# Identity Service
Identity-As-A-Service written from scratch in .Net Core using a CQRS architecture with a CosmosDB/Redis persistence layer and RSA signed JWT Tokens with public key distribution for authentication and claims.

# Architecture
![Architecture](https://github.com/INNVTV/Identity-Service/blob/master/_docs/imgs/architecture.png)

## Features
 * Microservices Ready
 * JWT token authentication w/ RSA Public/Private key pairs
 * CQRS Architecture using MediatR, FluentValidation and Custom Middleware
 * CosmosDB/Redis Persistence Layer
 * User/Role/Invitations Management
 * Private gRPC Endpoints
 * OpenAPI Endpoints w/ SwaggerUI
 * Well known endpoints for public key distribution
 * Redis caching
 * Serilog for structured logging
 * Max Attempt Lockouts, Invitations, Erc...
 * Tracking of login attempts, last logins, etc...
 * UserName or Email logins
 * Easy to refactor to your needs


# Architecture Notes
Project architecture is based off of my [.Net Core Clean Architecture](https://github.com/INNVTV/NetCore-Clean-Architecture) project. This means there is a strong CQRS pattern in place using MediatR.

# Security
A Primary and Secondary APIKey is used to authenticate calls to the **/api** endpoints. This is handled by the **ApiKeyAuthenticationMiddleware** class found in **Core.Infrastructure.Middleware.ApiKeyAuthentication** 

Non api calls are allowed to flow through unauthenticated do that users can interact with the public UI for login, invitation and password recovery actions.

**Note:** The secondary api key is included in order to make key rotations less impactful in a microservices enviornment.

## OpenAPI/Swagger
Since all OpenAPI endpoints are secured by ApiKey the Swagger UI will allow you to authorize your calls for debugging purposes. In the current version there is a green "Authorize button" found in the top right of the Swagger UI page.

## NSwag Generated Client Code
Be sure that have **Inject HttpClient via constructor** set to true so that X-API-KEY header can be passed into your client calls.

Here is an example of calling the API:

    var httpClient = new System.Net.Http.HttpClient();
    httpClient.DefaultRequestHeaders.Add("X-API-KEY", "X-API-KEY");

    var usersClient = new Services.IdentityService.UsersClient("http://localhost:53227", httpClient);
    var results = await usersClient.ListAsync(40, Services.IdentityService.OrderBy.CreatedDate, Services.IdentityService.OrderDirection.ASC, "");

### Shared Client Library
Shared client library for OpenAPI/Swagger/gRPC services are found in the "Utilities" folder.

## gRPC
gRPC services are partially built out for those that wish to use remote pocedure calls.

# JWT and RSA Key Generation

RSA keys can be generated using the **Utilities/Cryptography/RSAKeyGeneration** utility

## Public Keys API
    /api/public/keys

## Public Keys Uri
    /public/keys

### JSON Web Tokens (JWT)
For more on [JSON Web Tokens](https://jwt.io/) visit the project site.


