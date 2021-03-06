﻿using Core.Application.Authentication.Commands.GenerateRefreshToken;
using Core.Application.Authentication.Helpers;
using Core.Application.Authentication.Models;
using Core.Application.Users.Models.Documents;
using Core.Infrastructure.Configuration;
using Core.Infrastructure.Persistence.DocumentDatabase;
using Core.Infrastructure.Persistence.RedisCache;
using FluentValidation.Results;
using MediatR;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Application.Authentication.Commands.AuthenticateUser
{
    public class AuthenticateUserCommandHandler : IRequestHandler<AuthenticateUserCommand, AuthenticationResponse>
    {
        //MediatR will automatically inject dependencies
        private readonly IMediator _mediator;
        private readonly ICoreConfiguration _coreConfiguration;
        private readonly IDocumentContext _documentContext;
        private readonly IRedisContext _redisContext;

        public AuthenticateUserCommandHandler(IMediator mediator, IDocumentContext documentContext, IRedisContext redisContext, ICoreConfiguration coreConfiguration)
        {
            _mediator = mediator;
            _coreConfiguration = coreConfiguration;
            _documentContext = documentContext;
            _redisContext = redisContext;
        }

        public async Task<AuthenticationResponse> Handle(AuthenticateUserCommand request, CancellationToken cancellationToken)
        {

            // Validate our input
            AuthenticateUserValidator validator = new AuthenticateUserValidator();
            ValidationResult validationResult = validator.Validate(request);
            if (!validationResult.IsValid)
            {
                return new AuthenticationResponse(validationResult.Errors) { Message = "Please include both a username/email and a password" };
            }


            #region Setup our caching client and key

            IDatabase cache = _redisContext.ConnectionMultiplexer.GetDatabase();
            var attemptsKey = Common.Constants.CachingKeys.LoginAttempts(request.UserNameOrEmail);
            var attemptsCount = 0;

            #endregion

            #region Check lockouts and login attempts (Redis Cache)

            // Check attempts count on this username/email
            var attemptsCacheValue = cache.StringGet(attemptsKey);

            if (attemptsCacheValue.HasValue)
            {
                attemptsCount = Convert.ToInt32(attemptsCacheValue);
                if (attemptsCount >= _coreConfiguration.Logins.MaxAttemptsBeforeLockout)
                {
                    return new AuthenticationResponse { Message = "Too many attempts!" };
                }
            }

            #endregion


            //=========================================================================
            // DETERMINE AUTHENTICATION TYPE AND QUERY THE DOCUMENT STORE
            //=========================================================================

            #region get user from document store

            var authenticationType = "NameKey";
            var authenticationQuery = Common.Transformations.NameKey.Transform(request.UserNameOrEmail);

            if (request.UserNameOrEmail.Contains("@") && request.UserNameOrEmail.Contains("."))
            {
                authenticationType = "Email";
                authenticationQuery = request.UserNameOrEmail.ToLower().Trim();
            }

            // Create the query
            string sqlQuery = $"SELECT * FROM Documents d WHERE d.{ authenticationType } ='{ authenticationQuery }'";
            var sqlSpec = new SqlQuerySpec { QueryText = sqlQuery };

            // Generate collection uri
            Uri collectionUri = UriFactory.CreateDocumentCollectionUri(_documentContext.Settings.Database, _documentContext.Settings.Collection);

            // Generate FeedOptions/ParitionKey
            var feedOptions = new FeedOptions
            {
                PartitionKey = new PartitionKey(Common.Constants.DocumentType.User())
            };

            // Run query against the document store
            var getResult = _documentContext.Client.CreateDocumentQuery<UserDocumentModel>(
                collectionUri,
                sqlSpec,
                feedOptions
            );

            var userDocumentModel = getResult.AsEnumerable().FirstOrDefault();

            if (userDocumentModel == null)
            {
                #region Update login attempts (Redis Cache)

                cache.StringSet(attemptsKey, (attemptsCount+1), TimeSpan.FromHours(_coreConfiguration.Logins.LockoutTimespanHours), When.Always, CommandFlags.FireAndForget);

                #endregion

                return new AuthenticationResponse { Message = "Incorrect credentials" };
            }

            #endregion

            //=========================================================================
            // DETERMINE IF THE PASSWORD IS CORRECT
            //=========================================================================
            var authenticationGranted = Common.Encryption.PasswordHashing.ValidatePassword(request.Password, userDocumentModel.PasswordHash, userDocumentModel.PasswordSalt);

            if(!authenticationGranted)
            {
                #region Update login attempts (Redis Cache)

                cache.StringSet(attemptsKey, (attemptsCount+1), TimeSpan.FromHours(_coreConfiguration.Logins.LockoutTimespanHours), When.Always, CommandFlags.FireAndForget);

                #endregion

                return new AuthenticationResponse { Message = "Incorrect credentials" };

            }
            else
            {
                #region Clear login attempts (Redis Cache)

                if(attemptsCount > 0)
                {
                    cache.KeyDelete(attemptsKey, CommandFlags.FireAndForget);
                }

                #endregion

                //=========================================================================
                //
                //      BUILD THE JWT TOKEN, REFRESH TOKEN AND RETURN RESULTS
                //
                //=========================================================================


                var jwtTokenString = GenerateJwtToken.GenerateJwtTokenString(
                    _coreConfiguration,
                    userDocumentModel.Id,
                    userDocumentModel.UserName,
                    userDocumentModel.NameKey,
                    userDocumentModel.Email,
                    userDocumentModel.FirstName,
                    userDocumentModel.LastName,
                    userDocumentModel.Roles
                    );

                // Generate refresh token
                var refreshToken = string.Empty;
                try
                {
                    refreshToken = await _mediator.Send(new GenerateRefreshTokenCommand { UserId = userDocumentModel.Id });
                }
                catch(Exception ex)
                {
                    Log.Error("There was an error generating a refresh token for {loginString} during authentication {@ex}", request.UserNameOrEmail, ex);
                }
                


                //=========================================================================
                // UPDATE LAST LOGIN DATETIME ON USER
                //=========================================================================

                #region Update Last Login DateTime

                // Update field:
                userDocumentModel.LastLoginDate = DateTime.UtcNow;

                var documentUri = UriFactory.CreateDocumentUri(
               _documentContext.Settings.Database,
               _documentContext.Settings.Collection,
               userDocumentModel.Id);

                ResourceResponse<Document> result;

                try
                {
                    // Save the document to document store using the IDocumentContext dependency
                    result = await _documentContext.Client.ReplaceDocumentAsync(
                        documentUri,
                        userDocumentModel,
                        new RequestOptions { PartitionKey = new PartitionKey(Common.Constants.DocumentType.User().ToString()) }
                    );
                }
                catch (Exception ex)
                {
                    // throw DocumentStoreException (if a custom exception type is desired)
                    // ... Will be caught, logged and handled by the ExceptionHandlerMiddleware

                    // Pass exception up the chain:
                    throw ex;

                    // ...Or pass along as inner exception:
                    //throw new Exception("An error occured trying to use the document store", ex);
                }
                finally
                {
                    // Close any open connections, etc...
                }

                #endregion

                //=========================================================================
                // LOG THE ACTIVITY
                //=========================================================================
                var user = AutoMapper.Mapper.Map<Core.Domain.Entities.User>(userDocumentModel);
                Log.Information("User authenticated {@user}", user);

                return new AuthenticationResponse { isSuccess = true, JwtToken = jwtTokenString, RefreshToken = refreshToken, User = user, Message = "Authentication succeeded" };
            }
        }

        
    }
}
