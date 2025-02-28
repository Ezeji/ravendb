using System;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Raven.Client.Documents.Operations.Backups;
using xRetry;

namespace Tests.Infrastructure
{
    public class CustomS3RetryFactAttribute : RetryFactAttribute
    {
        private const string S3CredentialEnvironmentVariable = "CUSTOM_S3_SETTINGS";

        private static readonly S3Settings _s3Settings;

        public static S3Settings S3Settings => new S3Settings(_s3Settings);

        private static readonly string ParsingError;

        private static readonly bool EnvVariableMissing;

        static CustomS3RetryFactAttribute()
        {
            var strSettings = Environment.GetEnvironmentVariable(S3CredentialEnvironmentVariable);
            if (strSettings == null)
            {
                EnvVariableMissing = true;
                return;
            }

            if (string.IsNullOrEmpty(strSettings))
                return;

            try
            {
                _s3Settings = JsonConvert.DeserializeObject<S3Settings>(strSettings);
            }
            catch (Exception e)
            {
                ParsingError = e.ToString();
            }
        }

        public CustomS3RetryFactAttribute([CallerMemberName] string memberName = "", int maxRetries = 3, int delayBetweenRetriesMs = 0, params Type[] skipOnExceptions)
            : base(maxRetries, delayBetweenRetriesMs, skipOnExceptions)
        {
            //if (RavenTestHelper.IsRunningOnCI)
            //    return;

            if (EnvVariableMissing)
            {
                Skip = $"Test is missing '{S3CredentialEnvironmentVariable}' environment variable.";
                return;
            }

            if (string.IsNullOrEmpty(ParsingError) == false)
            {
                Skip = $"Failed to parse custom S3 settings, error: {ParsingError}";
                return;
            }

            if (_s3Settings == null)
            {
                Skip = $"S3 {memberName} tests missing S3 settings.";
            }
        }
    }
}
