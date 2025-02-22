using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Http.CloudFlare;
using NzbDrone.Core.ImportLists.Exceptions;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.ImportLists.AniList.List
{
    internal class AniListImport : AniListImportBase<AniListSettings>
    {
        public AniListImport(IImportListRepository netImportRepository,
                    IHttpClient httpClient,
                    IImportListStatusService importListStatusService,
                    IConfigService configService,
                    IParsingService parsingService,
                    Logger logger)
        : base(netImportRepository, httpClient, importListStatusService, configService, parsingService, logger)
        {
        }

        public override string Name => "AniList List";

        public override AniListRequestGenerator GetRequestGenerator()
        {
            return new AniListRequestGenerator()
            {
                Settings = Settings,
                ClientId = ClientId
            };
        }

        public override AniListParser GetParser()
        {
            return new AniListParser(Settings);
        }

        protected override IList<ImportListItemInfo> FetchItems(Func<IImportListRequestGenerator, ImportListPageableRequestChain> pageableRequestChainSelector, bool isRecent = false)
        {
            var releases = new List<ImportListItemInfo>();
            var url = string.Empty;

            try
            {
                var generator = GetRequestGenerator();
                var parser = GetParser();
                var pageIndex = 1;
                var hasNextPage = false;
                ImportListRequest currentRequest = null;

                // Anilist caps the result list to 50 items at maximum per query, so the data must be pulled in batches.
                // The number of pages are not known upfront, so the fetch logic must be changed to look at the returned page data.
                do
                {
                    // Build the query for the current page
                    currentRequest = generator.GetRequest(pageIndex);
                    url = currentRequest.Url.FullUri;

                    // Fetch and parse the response
                    var response = FetchImportListResponse(currentRequest);
                    var page = parser.ParseResponse(response, out var pageInfo).ToList();
                    releases.AddRange(page.Where(IsValidItem));

                    // Update page info
                    hasNextPage = pageInfo.HasNextPage; // server reports there is another page
                    pageIndex = pageInfo.CurrentPage + 1; // increment using the returned server index for the current page
                }
                while (hasNextPage);

                _importListStatusService.RecordSuccess(Definition.Id);
            }
            catch (WebException webException)
            {
                if (webException.Status == WebExceptionStatus.NameResolutionFailure ||
                    webException.Status == WebExceptionStatus.ConnectFailure)
                {
                    _importListStatusService.RecordConnectionFailure(Definition.Id);
                }
                else
                {
                    _importListStatusService.RecordFailure(Definition.Id);
                }

                if (webException.Message.Contains("502") || webException.Message.Contains("503") ||
                    webException.Message.Contains("timed out"))
                {
                    _logger.Warn("{0} server is currently unavailable. {1} {2}", this, url, webException.Message);
                }
                else
                {
                    _logger.Warn("{0} {1} {2}", this, url, webException.Message);
                }
            }
            catch (TooManyRequestsException ex)
            {
                if (ex.RetryAfter != TimeSpan.Zero)
                {
                    _importListStatusService.RecordFailure(Definition.Id, ex.RetryAfter);
                }
                else
                {
                    _importListStatusService.RecordFailure(Definition.Id, TimeSpan.FromHours(1));
                }

                _logger.Warn("API Request Limit reached for {0}", this);
            }
            catch (HttpException ex)
            {
                _importListStatusService.RecordFailure(Definition.Id);
                _logger.Warn("{0} {1}", this, ex.Message);
            }
            catch (RequestLimitReachedException)
            {
                _importListStatusService.RecordFailure(Definition.Id, TimeSpan.FromHours(1));
                _logger.Warn("API Request Limit reached for {0}", this);
            }
            catch (CloudFlareCaptchaException ex)
            {
                _importListStatusService.RecordFailure(Definition.Id);
                ex.WithData("FeedUrl", url);

                if (ex.IsExpired)
                {
                    _logger.Error(ex, "Expired CAPTCHA token for {0}, please refresh in import list settings.", this);
                }
                else
                {
                    _logger.Error(ex, "CAPTCHA token required for {0}, check import list settings.", this);
                }
            }
            catch (ImportListException ex)
            {
                _importListStatusService.RecordFailure(Definition.Id);
                _logger.Warn(ex, "{0}", url);
            }
            catch (Exception ex)
            {
                _importListStatusService.RecordFailure(Definition.Id);
                ex.WithData("FeedUrl", url);
                _logger.Error(ex, "An error occurred while processing feed. {0}", url);
            }

            return CleanupListItems(releases);
        }
    }
}
