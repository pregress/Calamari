﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Calamari.Aws.Exceptions;
using Calamari.Aws.Integration;
using Calamari.Aws.Integration.S3;
using Calamari.Aws.Util;
using Calamari.Deployment;
using Calamari.Deployment.Conventions;
using Calamari.Integration.FileSystem;
using Calamari.Integration.Substitutions;
using Octopus.CoreUtilities;
using Octopus.CoreUtilities.Extensions;

namespace Calamari.Aws.Deployment.Conventions
{
    public class UploadAwsS3Convention : IInstallConvention
    {
        private readonly ICalamariFileSystem fileSystem;
        private readonly IAwsEnvironmentGeneration awsEnvironmentGeneration;
        private readonly string bucket;
        private readonly S3TargetMode targetMode;
        private readonly IProvideS3TargetOptions optionsProvider;
        private readonly IFileSubstituter fileSubstituter;

        private static readonly HashSet<S3CannedACL> CannedAcls = new HashSet<S3CannedACL>(ConstantHelpers.GetConstantValues<S3CannedACL>());
        
        public UploadAwsS3Convention(ICalamariFileSystem fileSystem,
            IAwsEnvironmentGeneration awsEnvironmentGeneration,
            string bucket,
            S3TargetMode targetMode,
            IProvideS3TargetOptions optionsProvider,
            IFileSubstituter fileSubstituter
        )
        {
            this.fileSystem = fileSystem;
            this.awsEnvironmentGeneration = awsEnvironmentGeneration;
            this.bucket = bucket;
            this.targetMode = targetMode;
            this.optionsProvider = optionsProvider;
            this.fileSubstituter = fileSubstituter;
        }

        private static string ExceptionMessageWithFilePath(PutObjectRequest request, Exception exception)
        {
            return $"Failed to upload file {request.FilePath}. {exception.Message}";
        }

        private static string InvalidArgumentExceptionMessage(PutObjectRequest request, Exception exception)
        {
            //There isn't an associated error we can check for the Canned ACL so just check it against what we can determine
            //from the values in the SDK.
            string error = $"Failed to upload {request.FilePath}. An invalid argument was provided.";
            return !CannedAcls.Contains(request.CannedACL) ? $"{error} This is possibly due to the value specified for the canned ACL." : error;
        }

        //Errors we care about for each upload.
        private readonly Dictionary<string, Func<PutObjectRequest, Exception, string>> perFileUploadErrors = new Dictionary<string, Func<PutObjectRequest, Exception, string>>
        {
            { "RequestIsNotMultiPartContent", ExceptionMessageWithFilePath },
            { "UnexpectedContent", ExceptionMessageWithFilePath },
            { "MetadataTooLarge", ExceptionMessageWithFilePath },
            { "MaxMessageLengthExceeded", ExceptionMessageWithFilePath },
            { "KeyTooLongError", ExceptionMessageWithFilePath },
            { "SignatureDoesNotMatch", ExceptionMessageWithFilePath },
            { "InvalidStorageClass", ExceptionMessageWithFilePath },
            { "InvalidArgument",  InvalidArgumentExceptionMessage }
        };

        public void Install(RunningDeployment deployment)
        {
            //The bucket should exist at this point
            Guard.NotNull(deployment, "deployment can not be null");

            var options = optionsProvider.GetOptions(targetMode);
            AmazonS3Client Factory() => ClientHelpers.CreateS3Client(awsEnvironmentGeneration);

            try
            {
                UploadAll(options, Factory, deployment).Tee(responses =>
                {
                    foreach (var response in responses)
                    {
                        if (response.IsSuccess())
                        {
                            Log.Info($"Saving variable \"Octopus.Action[{deployment.Variables["Octopus.Action.Name"]}].Output.Files[{response.BucketKey}]\"");
                            Log.SetOutputVariable($"Files[{response.BucketKey}]", response.Version);
                        }
                    }
                });
            }
            catch (AmazonS3Exception exception)
            {
                if (exception.ErrorCode == "AccessDenied")
                {
                    throw new PermissionException("The AWS account used to perform the operation does not have the " +
                        $"the required permissions to upload to bucket {bucket}");
                }

                throw new UnknownException($"An unrecognised {exception.ErrorCode} error was thrown while uploading to bucket {bucket}");
            }
            catch (AmazonServiceException exception)
            {
                HandleAmazonServiceException(exception);
                throw;
            }
        }

        private IEnumerable<S3UploadResult> UploadAll(IEnumerable<S3TargetPropertiesBase> options, Func<AmazonS3Client> clientFactory, RunningDeployment deployment)
        {
            foreach (var option in options)
            {
                switch (option)
                {
                    case S3PackageOptions package:
                        yield return UploadUsingPackage(clientFactory, deployment, package);
                        break;
                    case S3SingleFileSlectionProperties selection:
                        yield return UploadSingleFileSelection(clientFactory, deployment, selection);
                        break;
                    case S3MultiFileSelectionProperties selection:
                        foreach (var response in UploadMultiFileSelection(clientFactory, deployment, selection))
                        {
                            yield return response;
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Uploads multiple files given the globbing patterns provided by the selection properties.
        /// </summary>
        /// <param name="clientFactory"></param>
        /// <param name="deployment"></param>
        /// <param name="selection"></param>
        private IEnumerable<S3UploadResult> UploadMultiFileSelection(Func<AmazonS3Client> clientFactory, RunningDeployment deployment, S3MultiFileSelectionProperties selection)
        {
            Guard.NotNull(deployment, "Deployment may not be null");
            Guard.NotNull(selection, "Mutli file selection properties may not be null");
            Guard.NotNull(clientFactory, "Client factory must not be null");

            var files = fileSystem.EnumerateFilesWithGlob(deployment.StagingDirectory, selection.Pattern).ToList();
            if (!files.Any())
            {
                Log.Info($"The glob pattern '{selection.Pattern}' didn't match any files. Nothing was uploaded to S3.");
                yield break;
            }

            Log.Info($"Glob pattern '{selection.Pattern}' matched {files.Count} files.");
            var substitutionPatterns = selection.VariableSubstitutionPatterns?.Split(new[] { "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries) ?? new string[0];
            
            new SubstituteInFilesConvention(fileSystem, fileSubstituter,
                    _ => substitutionPatterns.Any(),
                    _ => substitutionPatterns)
                .Install(deployment);

            foreach (var matchedFile in files)
            {
                yield return CreateRequest(matchedFile, $"{selection.BucketKeyPrefix}{fileSystem.GetFileName(matchedFile)}", selection)
                    .Tee(x => LogPutObjectRequest(matchedFile, x))
                    .Map(x => HandleUploadRequest(clientFactory(), x));
            }
        }

        /// <summary>
        /// Uploads a single file with the given properties
        /// </summary>
        /// <param name="clientFactory"></param>
        /// <param name="deployment"></param>
        /// <param name="selection"></param>
        public S3UploadResult UploadSingleFileSelection(Func<AmazonS3Client> clientFactory, RunningDeployment deployment, S3SingleFileSlectionProperties selection)
        {
            Guard.NotNull(deployment, "Deployment may not be null");
            Guard.NotNull(selection, "Single file selection properties may not be null");
            Guard.NotNull(clientFactory, "Client factory must not be null");

            var filePath = Path.Combine(deployment.CurrentDirectory, selection.Path);

            if (!fileSystem.FileExists(filePath))
            {
                throw new FileNotFoundException($"The file {selection.Path} could not be found in the package.");
            }

            new SubstituteInFilesConvention(fileSystem, fileSubstituter, 
                _ => selection.PerformVariableSubstitution, 
                _ => new List<string>{ filePath })
                .Install(deployment);
    
            return CreateRequest(filePath, selection.BucketKey, selection)
                    .Tee(x => LogPutObjectRequest(filePath, x))
                    .Map(x => HandleUploadRequest(clientFactory(), x));
        }

        /// <summary>
        /// Uploads the given package file with the provided package options
        /// </summary>
        /// <param name="clientFactory"></param>
        /// <param name="deployment"></param>
        /// <param name="options"></param>
        public S3UploadResult UploadUsingPackage(Func<AmazonS3Client> clientFactory, RunningDeployment deployment, S3PackageOptions options)
        {
            Guard.NotNull(deployment, "Deployment may not be null");
            Guard.NotNull(options, "Package options may not be null");
            Guard.NotNull(clientFactory, "Client factory must not be null");

            return CreateRequest(deployment.PackageFilePath, options.BucketKey, options)
                .Tee(x => LogPutObjectRequest("entire package", x))
                .Map(x => HandleUploadRequest(clientFactory(), x));
        }

        /// <summary>
        /// Creates an upload file request based on the s3 target properties for a given file and bucket key. 
        /// </summary>
        /// <param name="path"></param>
        /// <param name="bucketKey"></param>
        /// <param name="properties"></param>
        /// <returns>PutObjectRequest with all information including metadata and tags from provided properties</returns>
        private PutObjectRequest CreateRequest(string path, string bucketKey, S3TargetPropertiesBase properties)
        {
            Guard.NotNullOrWhiteSpace(path, "The given path may not be null");
            Guard.NotNullOrWhiteSpace(bucket, "The provided bucket key may not be null");
            Guard.NotNull(properties, "Target properties may not be null");

            return new PutObjectRequest
                {
                    BucketName = bucket?.Trim(),
                    FilePath = path,
                    Key = bucketKey?.Trim(),
                    StorageClass = S3StorageClass.FindValue(properties.StorageClass?.Trim()),
                    CannedACL = S3CannedACL.FindValue(properties.CannedAcl?.Trim())
            }
                .WithMetadata(properties)
                .WithTags(properties);
        }

        /// <summary>
        /// Displays the current information regarding the object that will be uploaded to the user.
        /// </summary>
        /// <param name="fileOrPackageDescription"></param>
        /// <param name="request"></param>
        private static void LogPutObjectRequest(string fileOrPackageDescription, PutObjectRequest request)
        {
            Log.Info($"Uploading {fileOrPackageDescription} to bucket {request.BucketName} with key {request.Key}.");
        }

        /// <summary>
        /// Display an warning message to the user
        /// </summary>
        /// <param name="errorCode">The error message code</param>
        /// <param name="message">The error message body</param>
        /// <returns>true if it was displayed, and false otherwise</returns>
        private static void DisplayWarning(string errorCode, string message)
        {
            Log.Warn(
                errorCode + ": " + message + "\n" +
                "For more information visit https://g.octopushq.com/AwsS3Upload#" +
                errorCode.ToLower());
        }

        /// <summary>
        /// Handle the file upload request throwing exceptions only on errors from AWS which is critical enough to fail
        /// the entire deployment i.e. access denied while per file errors will result in warnings.
        /// </summary>
        /// <param name="client"></param>
        /// <param name="request"></param>
        private S3UploadResult HandleUploadRequest(AmazonS3Client client, PutObjectRequest request)
        {
            try
            {
                return new S3UploadResult(request, Maybe<PutObjectResponse>.Some(client.PutObject(request)));
            }
            catch (AmazonS3Exception ex)
            {
                if (ex.ErrorCode == "AccessDenied")
                    throw new PermissionException(
                        "AWS-S3-ERROR-0002: The AWS account used to perform the operation does not have " +
                        "the required permissions to upload to the bucket.\n" +
                        ex.Message + "\n" +
                        "For more information visit https://g.octopushq.com/AwsS3Upload#aws-s3-error-0002");

                if (!perFileUploadErrors.ContainsKey(ex.ErrorCode)) throw;
                perFileUploadErrors[ex.ErrorCode](request, ex).Tee(Log.Warn);
            }
            return new S3UploadResult(request, Maybe<PutObjectResponse>.None);
        }

        /// <summary>
        /// The AmazonServiceException can hold additional information that is useful to include in
        /// the log.
        /// </summary>
        /// <param name="exception">The exception</param>
        private void HandleAmazonServiceException(AmazonServiceException exception)
        {
            ((exception.InnerException as WebException)?
             .Response?
             .GetResponseStream()?
             .Map(stream => new StreamReader(stream).ReadToEnd())
             .Map(message => "An exception was thrown while contacting the AWS API.\n" + message)
             ?? "An exception was thrown while contacting the AWS API.")
                .Tee(message => DisplayWarning("AWS-S3-ERROR-0001", message));
        }
    }
}
