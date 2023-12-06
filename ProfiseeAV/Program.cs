using CommandLine;
using NLog;
using Profisee.MasterDataMaestro.Services.Contracts.DataContracts.Common.Expression;
using Profisee.MasterDataMaestro.Services.DataContracts;
using Profisee.MasterDataMaestro.Services.DataContracts.MasterDataServices;
using Profisee.MasterDataMaestro.Services.MessageContracts;
using Profisee.Services.Sdk.AcceleratorFramework;
using Profisee.Services.Sdk.Common.Contracts.DataContext;
using System.Collections.ObjectModel;

namespace ProfiseeAV
{
    class Program
    {
        public class Options
        {
            [Option('s', "strategy", Required = true, HelpText = "Address Verification Strategy Name")]
            public string AvStrategyName { get; set; }

            [Option('c', "clientid", Required = true, HelpText = "ClientId for the matching strategy to process under")]
            public string ClientId { get; set; }

            [Option('u', "uri", Required = true, HelpText = "The Profisee URI")]
            public string URI { get; set; }

            [Option('m', "mailing", Required = false, HelpText = "Include mailing address verification (True/False)")]
            public bool MailingAddressVerification { get; set; }

            [Option('p', "phone", Required = false, HelpText = "Include phone verification (True/False)")]
            public bool PhoneNumberVerifcaion { get; set; }

            [Option('e', "email", Required = false, HelpText = "Include email verification (True/False)")]
            public bool EmailVerifcaion { get; set; }

            [Option('n', "name", Required = false, HelpText = "Include name parsing (True/False)")]
            public bool NameParsing { get; set; }

            [Option('l', "location", Required = false, HelpText = "Include location verfication (True/False)")]
            public bool LocationVerification { get; set; }

        }

        private static bool ReturnError = false;
        private static Logger Logger = LogManager.GetCurrentClassLogger();

        public const string AssemblyVersion = "2.0.0.0";
        public const string AssemblyFileVersion = "2.0.0.0";

        static int Main(string[] args) {
            try {
                Parser.Default.ParseArguments<Options>(args).WithParsed(options => {
                    WriteDiagnosticInformation(options);

                    Logger.Info("---Logging into MDM Server---");

                    var mdmSrc = new MdmSource();
                    mdmSrc.Connect(options.URI, options.ClientId);

                    var model = mdmSrc.GetModel();
                    if (model == null) { throw new Exception("Could not retrieve model from server"); }

                    Collection<ExternalServiceType> processingServices = GetProcessingServices(options);

                    AddressStrategy avStrategy = GetAddressStrategy(options, model);

                    var entity = model.GetEntity(avStrategy.EntityId);
                    ExecuteAddressVerification(model, processingServices, avStrategy, entity);

                });
            } catch (Exception ex) {
                Console.Error.WriteLine(ex);
                ReturnError = true;
            }

            return ReturnError ? 0 : 1;
        }

        private static void WriteDiagnosticInformation(Options options) {
            Logger.Debug("---Diagnostic Information---");
            Logger.Debug($"Mailing verifcation: {options.MailingAddressVerification}");
            Logger.Debug($"Phone verifcation: {options.PhoneNumberVerifcaion}");
            Logger.Debug($"Email verification: {options.EmailVerifcaion}");
            Logger.Debug($"Location verification: {options.LocationVerification}");
            Logger.Debug($"Name parsing: {options.NameParsing}");
        }

        private static AddressStrategy GetAddressStrategy(Options options, MdmModel model) {
            var getAddressStrategiesRequest = new GetAddressStrategiesRequest();
            var getAddressStratgeiesResponse = model.GetAddressStrategies(getAddressStrategiesRequest);
            if (getAddressStratgeiesResponse.OperationResult.Errors.Count > 0) {
                foreach (var error in getAddressStratgeiesResponse.OperationResult.Errors) {
                    throw new Exception($"Could not retrieve AV strategies from model; {error.Code} - {error.Context} - {error.Description}");
                }
            }

            var avStrategy = getAddressStratgeiesResponse.Strategies.FirstOrDefault(s => s.Identifier.Name == options.AvStrategyName);
            if (avStrategy == null) { throw new Exception($"Could not find address strategy {options.AvStrategyName}"); }

            return avStrategy;
        }

        private static Collection<ExternalServiceType> GetProcessingServices(Options options) {
            var processingServices = new Collection<ExternalServiceType>();
            if (options.MailingAddressVerification)
                processingServices.Add(ExternalServiceType.MailingAddressVerification);
            if (options.PhoneNumberVerifcaion)
                processingServices.Add(ExternalServiceType.PhoneVerification);
            if (options.NameParsing)
                processingServices.Add(ExternalServiceType.NameParsing);
            if (options.LocationVerification)
                processingServices.Add(ExternalServiceType.LocationVerification);
            if (options.EmailVerifcaion)
                processingServices.Add(ExternalServiceType.EmailVerification);
            return processingServices;
        }


        private static void ExecuteAddressVerification(MdmModel model, Collection<ExternalServiceType> processingServices, AddressStrategy avStrategy, MdmEntity entity) {
            Logger.Info("---Executing Address Verfication---");

            var browseEntityContext = new BrowseEntityContext() { IdentityOnly = true };
            browseEntityContext.FilterExpression = Filter.On("BatchProcessFlag", BinaryOperator.Equals, "True").FilterExpression;

            var entityMembersInfo = entity.GetInformation("VERSION_1", browseEntityContext);

            Logger.Debug($"Processing for: {entityMembersInfo.TotalMemberCount} members");
            Logger.Debug($"Page Size: {browseEntityContext.PageSize}");
            Logger.Debug($"Total Pages: {entityMembersInfo.TotalPages}");

            var errors = new Collection<Profisee.MasterDataMaestro.Services.DataContracts.Error>();
            var errorLock = new object();

            var responseMembers = new Collection<Member>();
            var responseMembersLock = new object();

            for (browseEntityContext.PageNumber = 1; browseEntityContext.PageNumber <= entityMembersInfo.TotalPages; browseEntityContext.PageNumber++) {
                var members = entity.GetMdmMembers(browseEntityContext);

                Parallel.ForEach(members, member => {
                    var getAddressRequest = new GetAddressRequest() { Member = member.Member, ProcessingServices = processingServices, StrategyId = avStrategy.Identifier };
                    var getAddressResponse = model.GetAddress(getAddressRequest);
                    if (getAddressResponse.OperationResult.Errors.Count > 0) {
                        foreach (var error in getAddressResponse.OperationResult.Errors) {
                            lock (errorLock) {
                                errors.Add(error);
                            }
                        }
                    } else {
                        lock (responseMembersLock) {
                            responseMembers.Add(getAddressResponse.Members[0]);
                        }
                    }
                });
            }

            //save the updates back to disk
            var mergeMemberErrors = entity.MergeMembers(responseMembers);
            foreach (var mergeMemberError in mergeMemberErrors) { errors.Add(mergeMemberError); }

            foreach (var error in errors) {
                Logger.Error($"An error occured: {error.Code} - {error.Context} - {error.Description}");
                ReturnError = true;
            }
        }


    }
}
