﻿using ApiReviewDotNet.Data;
using ApiReviewDotNet.Services.GitHub;
using ApiReviewDotNet.Services.YouTube;

using Markdig;

using Octokit;

using SendGrid;
using SendGrid.Helpers.Mail;

using EmailAddress = SendGrid.Helpers.Mail.EmailAddress;

namespace ApiReviewDotNet.Services;

public sealed class SummaryPublishingService
{
    private readonly ILogger<SummaryPublishingService> _logger;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _configuration;
    private readonly RepositoryGroupService _repositoryGroupService;
    private readonly GitHubClientFactory _clientFactory;
    private readonly YouTubeServiceFactory _youTubeServiceFactory;

    public SummaryPublishingService(ILogger<SummaryPublishingService> logger,
                                    IWebHostEnvironment env,
                                    IConfiguration configuration,
                                    RepositoryGroupService repositoryGroupService,
                                    GitHubClientFactory clientFactory,
                                    YouTubeServiceFactory youTubeServiceFactory)
    {
        _logger = logger;
        _env = env;
        _configuration = configuration;
        _repositoryGroupService = repositoryGroupService;
        _clientFactory = clientFactory;
        _youTubeServiceFactory = youTubeServiceFactory;
    }

    public async Task<ApiReviewPublicationResult> PublishAsync(ApiReviewSummary summary)
    {
        if (!summary.Items.Any())
            return ApiReviewPublicationResult.Failed();

        var group = _repositoryGroupService.Get(summary.RepositoryGroup);
        if (group is null)
            return ApiReviewPublicationResult.Failed();

        if (_env.IsDevelopment())
        {
            await UpdateCommentsDevAsync(summary);
        }
        else
        {
            // Apparently, we can't easily modify video descriptions in the cloud.
            // If someone has a fix for that, I'd be massively thankful.
            //
            // await UpdateVideoDescriptionAsync(summary);
            await UpdateCommentsAsync(summary);
        }

        var url = await CommitAsync(group, summary);
        await SendEmailAsync(group, summary);
        return ApiReviewPublicationResult.Suceess(url);
    }

    private async Task SendEmailAsync(RepositoryGroup group, ApiReviewSummary summary)
    {
        if (group.MailingList is null)
            return;

        var key = _configuration["SendGridKey"];
        var date = summary.Items.First().FeedbackDateTime.Date;
        var subject = $"API Review Notes {date:d}";
        var markdown = GetMarkdown(summary);
        var body = Markdown.ToHtml(markdown);
        var msg = new SendGridMessage();
        msg.SetFrom(new EmailAddress("notes@apireview.net", ".NET API Reviews"));
        msg.AddTo(new EmailAddress(group.MailingList));
        msg.SetReplyTo(new EmailAddress(group.MailingReplyTo));
        msg.SetSubject(subject);
        msg.AddContent(MimeType.Html, body);

        try
        {
            var client = new SendGridClient(key);
            await client.SendEmailAsync(msg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending email: {Message}", ex.Message);
        }
    }

    private async Task UpdateVideoDescriptionAsync(ApiReviewSummary summary)
    {
        if (summary.Video is null)
            return;

        using var descriptionBuilder = new StringWriter();
        foreach (var item in summary.Items)
        {
            var tc = item.TimeCode;
            descriptionBuilder.WriteLine($"{tc.Hours:00}:{tc.Minutes:00}:{tc.Seconds:00} - {item.Decision}: {item.Issue.Title} {item.FeedbackUrl}");
        }

        var description = descriptionBuilder.ToString()
                                            .Replace("<", "(")
                                            .Replace(">", ")");

        var service = _youTubeServiceFactory.Create();

        var listRequest = service.Videos.List("snippet");
        listRequest.Id = summary.Video.Id;
        var listResponse = await listRequest.ExecuteAsync();

        var video = listResponse.Items[0];
        video.Snippet.Description = description;

        var updateRequest = service.Videos.Update(video, "snippet");
        await updateRequest.ExecuteAsync();
    }

    private async Task UpdateCommentsAsync(ApiReviewSummary summary)
    {
        var github = await _clientFactory.CreateForAppAsync();

        foreach (var item in summary.Items)
        {
            var videoUrl = summary.GetVideoUrl(item.TimeCode);

            if (item.FeedbackId is not null && videoUrl is not null)
            {
                var updatedMarkdown = $"[Video]({videoUrl})\n\n{item.FeedbackMarkdown}";
                var commentId = Convert.ToInt64(item.FeedbackId);
                await github.Issue.Comment.Update(item.Issue.Owner, item.Issue.Repo, commentId, updatedMarkdown);
            }
        }
    }

    private async Task UpdateCommentsDevAsync(ApiReviewSummary summary)
    {
        var (owner, repo) = _repositoryGroupService.Repositories.First();

        if (!summary.Items.All(i => i.Issue.Owner == owner &&
                                    i.Issue.Repo == repo))
            return;

        var github = await _clientFactory.CreateForAppAsync();

        foreach (var item in summary.Items)
        {
            if (item.FeedbackId is not null)
            {
                var status = item.Decision.ToString();
                var updatedMarkdown = $"[Video]({status})\n\n{item.FeedbackMarkdown}";
                var commentId = Convert.ToInt64(item.FeedbackId);
                await github.Issue.Comment.Update(item.Issue.Owner, item.Issue.Repo, commentId, updatedMarkdown);
            }
        }
    }

    private async Task<string> CommitAsync(RepositoryGroup group, ApiReviewSummary summary)
    {
        var (owner, repo) = group.NotesRepo;
        var branch = ApiReviewConstants.ApiReviewsBranch;
        var head = $"heads/{branch}";
        var date = summary.Items.First().FeedbackDateTime.DateTime;
        var markdown = $"# API Review {date:d}\n\n{GetMarkdown(summary)}";
        var path = $"{date.Year}/{date.Month:00}-{date.Day:00}-{group.NotesSuffix}/README.md";
        var commitMessage = $"Add review notes for {date:d}";

        var github = await _clientFactory.CreateForAppAsync();
        var masterReference = await github.Git.Reference.Get(owner, repo, head);
        var latestCommit = await github.Git.Commit.Get(owner, repo, masterReference.Object.Sha);

        var recursiveTreeResponse = await github.Git.Tree.GetRecursive(owner, repo, latestCommit.Tree.Sha);
        var file = recursiveTreeResponse.Tree.SingleOrDefault(t => t.Path == path);

        if (file is null)
        {
            var newTreeItem = new NewTreeItem
            {
                Mode = "100644",
                Path = path,
                Content = markdown
            };

            var newTree = new NewTree
            {
                BaseTree = latestCommit.Tree.Sha
            };
            newTree.Tree.Add(newTreeItem);

            var newTreeResponse = await github.Git.Tree.Create(owner, repo, newTree);
            var newCommit = new NewCommit(commitMessage, newTreeResponse.Sha, latestCommit.Sha);
            var newCommitResponse = await github.Git.Commit.Create(owner, repo, newCommit);

            var newReference = new ReferenceUpdate(newCommitResponse.Sha);
            await github.Git.Reference.Update(owner, repo, head, newReference);
        }

        var url = $"https://github.com/{owner}/{repo}/blob/{branch}/{path}";
        return url;
    }

    private static string GetMarkdown(ApiReviewSummary summary)
    {
        var noteWriter = new StringWriter();

        foreach (var item in summary.Items)
        {
            noteWriter.WriteLine($"## {item.Issue.Title}");
            noteWriter.WriteLine();
            noteWriter.Write($"**{item.Decision}** | [#{item.Issue.Repo}/{item.Issue.Id}]({item.FeedbackUrl})");

            var videoUrl = summary.GetVideoUrl(item.TimeCode);
            if (videoUrl is not null)
                noteWriter.Write($" | [Video]({videoUrl})");

            noteWriter.WriteLine();
            noteWriter.WriteLine();

            if (item.FeedbackMarkdown is not null)
            {
                noteWriter.Write(item.FeedbackMarkdown);
                noteWriter.WriteLine();
            }
        }

        return noteWriter.ToString();
    }
}
